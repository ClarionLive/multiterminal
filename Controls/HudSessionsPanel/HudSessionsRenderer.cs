using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based session timeline showing recent sessions for the current project.
    /// Displays agent name, session type, timestamps, and summary.
    /// </summary>
    public class HudSessionsRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _projectPath;
        private string _pendingJson;

        public event EventHandler<double> ZoomChanged;

        public HudSessionsRenderer()
        {
            SuspendLayout();
            BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            Name = "HudSessionsRenderer";
            Visible = false;

            _webView = new WebView2 { Dock = DockStyle.Fill, Name = "sessionsWebView" };
            Controls.Add(_webView);
            ResumeLayout(false);

            VisibleChanged += (s, e) => { if (Visible && !_isInitialized && !_isInitializing) InitializeWebView(); };
        }

        private async void InitializeWebView()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? System.Drawing.Color.FromArgb(26, 26, 46) : System.Drawing.Color.FromArgb(245, 245, 245);
                var s = _webView.CoreWebView2.Settings;
                s.IsScriptEnabled = true; s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false; s.IsStatusBarEnabled = false;
                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                string htmlPath = FindHtml("Controls/HudSessionsPanel/hud-sessions.html", "HudSessionsPanel/hud-sessions.html");
                if (File.Exists(htmlPath)) _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                else _isInitializing = false;
            }
            catch { _isInitializing = false; }
        }

        private string FindHtml(params string[] relativePaths)
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var rel in relativePaths)
            {
                string p = Path.Combine(dir, rel);
                if (File.Exists(p)) return p;
            }
            return Path.Combine(dir, relativePaths[0]);
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (!doc.RootElement.TryGetProperty("type", out var t)) return;
                string msgType = t.GetString();

                if (msgType == "ready")
                {
                    _isInitialized = true; _isInitializing = false;
                    PostJson(new { type = "theme", isDark = _isDarkTheme });
                    if (_pendingJson != null) { PostRaw(_pendingJson); _pendingJson = null; }
                    else RefreshSessions();
                    _webView.ZoomFactorChanged += (s, ev) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                    if (Math.Abs(_pendingZoom - 1.0) > 0.01) _webView.ZoomFactor = _pendingZoom;
                }
                else if (msgType == "get_session_detail")
                {
                    if (doc.RootElement.TryGetProperty("sessionId", out var sidEl))
                    {
                        string sessionId = sidEl.GetString();
                        if (!string.IsNullOrEmpty(sessionId))
                            System.Threading.Tasks.Task.Run(() =>
                            {
                                LoadSessionDetail(sessionId);
                            });
                    }
                }
            }
            catch { }
        }

        public void Initialize(MessageBroker broker)
        {
            _broker = broker;
            if (_broker != null)
            {
                _broker.SessionLineageUpdated -= OnSessionLineageUpdated;
                _broker.SessionLineageUpdated += OnSessionLineageUpdated;
            }
        }

        public void SetProject(string projectPath)
        {
            _projectPath = projectPath;
            _pendingJson = null; // Clear stale pending data (e.g., no_project queued before project was set)
            if (_isInitialized) RefreshSessions();
        }

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized) PostJson(new { type = "theme", isDark });
        }

        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null) _webView.ZoomFactor = zoom;
        }

        public void RefreshSessions()
        {
            var lineageService = _broker?.SessionLineageService;
            if (lineageService == null || string.IsNullOrEmpty(_projectPath))
            {
                Send(new { type = "no_project" });
                return;
            }

            try
            {
                string claudeFolder = SessionLineageService.GetClaudeProjectFolder(_projectPath);
                if (string.IsNullOrEmpty(claudeFolder))
                {
                    Send(new { type = "sessions", sessions = Array.Empty<object>() });
                    return;
                }

                var sessions = lineageService.GetSessionsByFolder(claudeFolder, 20);
                var items = sessions.Select(s =>
                {
                    // Parse summary into topic + stats parts
                    string topic = null;
                    string stats = null;
                    if (!string.IsNullOrEmpty(s.Summary))
                    {
                        int dashIdx = s.Summary.IndexOf(" \u2014 ");
                        if (dashIdx > 0)
                        {
                            string candidateTopic = s.Summary.Substring(0, dashIdx);
                            stats = s.Summary.Substring(dashIdx + 3);
                            // Discard garbage topics from old heuristic summaries
                            if (!SessionLineageService.IsNoisyUserMessagePublic(candidateTopic))
                                topic = candidateTopic;
                        }
                        else
                        {
                            // Single-part summary — if it looks like stats, put it there
                            if (s.Summary.Contains(" edit") || s.Summary.Contains(" read") ||
                                s.Summary.Contains(" search") || s.Summary.Contains(" build") ||
                                s.Summary.Contains("Brief session") || s.Summary.Contains(" messages"))
                                stats = s.Summary;
                            else if (!SessionLineageService.IsNoisyUserMessagePublic(s.Summary))
                                topic = s.Summary;
                        }
                    }

                    // Calculate duration
                    string duration = null;
                    if (!string.IsNullOrEmpty(s.StartedAt) && !string.IsNullOrEmpty(s.EndedAt))
                    {
                        if (DateTime.TryParse(s.StartedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var start) &&
                            DateTime.TryParse(s.EndedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var end))
                        {
                            var span = end - start;
                            if (span.TotalHours >= 1) duration = $"{(int)span.TotalHours}h {span.Minutes}m";
                            else if (span.TotalMinutes >= 1) duration = $"{(int)span.TotalMinutes}m";
                            else duration = "<1m";
                        }
                    }

                    return new
                    {
                        sessionId = s.SessionId,
                        agentName = s.AgentName,
                        sessionType = s.SessionType,
                        topic,
                        stats,
                        duration,
                        startedAt = s.StartedAt,
                        endedAt = s.EndedAt
                    };
                }).ToArray();

                Send(new { type = "sessions", sessions = items });
            }
            catch
            {
                Send(new { type = "sessions", sessions = Array.Empty<object>() });
            }
        }

        /// <summary>
        /// Loads session detail (messages) for the expanded card view.
        /// </summary>
        private void LoadSessionDetail(string sessionId)
        {
            var lineageService = _broker?.SessionLineageService;
            if (lineageService == null || string.IsNullOrEmpty(sessionId))
            {
                Send(new { type = "session_detail", sessionId = sessionId, messages = Array.Empty<object>() });
                return;
            }

            try
            {
                var messages = lineageService.GetSessionMessagesBySessionId(sessionId, 200);

                // Extract meaningful messages: user prompts and assistant text (skip tool calls/results)
                var items = messages
                    .Where(m => !string.IsNullOrWhiteSpace(m.Content) &&
                                string.IsNullOrEmpty(m.ToolName) &&
                                m.Content.Length > 5)
                    .Select(m =>
                    {
                        string content = m.Content.Trim();
                        // Skip noisy/boilerplate messages
                        if (m.Role == "user" && SessionLineageService.IsNoisyUserMessagePublic(content))
                            return null;
                        // Truncate very long messages
                        if (content.Length > 300) content = content.Substring(0, 297) + "...";
                        return new
                        {
                            role = m.Role,
                            content,
                            toolName = m.ToolName
                        };
                    })
                    .Where(m => m != null)
                    .Take(15) // Cap at 15 messages for the detail view
                    .ToArray();

                // Also extract edited files from tool call messages
                var editedFiles = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in messages)
                {
                    if (m.ToolName == "Edit" || m.ToolName == "Write")
                    {
                        ExtractFileNameForDetail(m.Content, editedFiles);
                    }
                }

                Send(new
                {
                    type = "session_detail",
                    sessionId,
                    messages = items,
                    editedFiles = editedFiles.OrderBy(f => f).ToArray()
                });
            }
            catch
            {
                Send(new { type = "session_detail", sessionId, messages = Array.Empty<object>(), editedFiles = Array.Empty<string>() });
            }
        }

        /// <summary>
        /// Extracts a filename from tool call content for the detail view.
        /// </summary>
        private static void ExtractFileNameForDetail(string content, System.Collections.Generic.HashSet<string> files)
        {
            if (string.IsNullOrEmpty(content)) return;
            var lines = content.Split('\n', 3);
            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if ((trimmed.Contains(":\\") || trimmed.Contains(":/") || trimmed.StartsWith("/")) && trimmed.Contains("."))
                {
                    string path = trimmed.Split(new[] { ' ', '\t', '"' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(p => p.Contains(".") && (p.Contains("\\") || p.Contains("/")));
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { files.Add(System.IO.Path.GetFileName(path)); } catch { }
                    }
                    return;
                }
            }
        }

        private void OnSessionLineageUpdated(object sender, string sessionId)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => OnSessionLineageUpdated(sender, sessionId))); return; }
            RefreshSessions();
        }

        private void Send(object data)
        {
            string json = JsonSerializer.Serialize(data);
            if (_isInitialized) PostRaw(json); else _pendingJson = json;
        }

        private void PostJson(object d)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try { _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(d)); } catch { }
        }

        private void PostRaw(string json)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try { _webView.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_broker != null) _broker.SessionLineageUpdated -= OnSessionLineageUpdated;
                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null) _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.Dispose(); _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
