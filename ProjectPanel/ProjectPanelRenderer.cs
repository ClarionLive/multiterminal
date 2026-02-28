using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.ProjectPanel
{
    /// <summary>
    /// WebView2-based renderer for the project panel.
    /// Provides a modern, styled UI for project information.
    /// </summary>
    public class ProjectPanelRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private string _pendingMessage;
        private bool _isDarkTheme = true;

        /// <summary>
        /// Event fired when a prompt should be pasted to the terminal.
        /// </summary>
        public event EventHandler<string> PastePromptRequested;

        /// <summary>
        /// Event fired when a path should be copied to clipboard.
        /// </summary>
        public event EventHandler<string> CopyPathRequested;

        /// <summary>
        /// Event fired when WebView2 is ready.
        /// </summary>
        public event EventHandler Ready;

        /// <summary>
        /// Event fired when user clicks a session to open it.
        /// </summary>
        public event EventHandler<string> OpenSessionRequested;

        /// <summary>
        /// Event fired when user searches for sessions.
        /// </summary>
        public event EventHandler<string> SearchSessionsRequested;

        /// <summary>
        /// Event fired when user requests to sync sessions from Claude CLI.
        /// </summary>
        public event EventHandler SyncSessionsRequested;

        /// <summary>
        /// Event fired when user expands a session to view messages.
        /// </summary>
        public event EventHandler<string> GetSessionMessagesRequested;

        /// <summary>
        /// Gets whether the renderer is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        public ProjectPanelRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);
            Name = "ProjectPanelRenderer";
            Size = new Size(300, 400);

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

            string path = Path.Combine(assemblyDir, "ProjectPanel", "panel.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "ProjectPanel", "panel.html");
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
                        _isInitialized = true;
                        _isInitializing = false;
                        SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");
                        if (!string.IsNullOrEmpty(_pendingMessage))
                        {
                            SendMessage(_pendingMessage);
                            _pendingMessage = null;
                        }
                        Ready?.Invoke(this, EventArgs.Empty);
                        break;

                    case "pastePrompt":
                        PastePromptRequested?.Invoke(this, message.PromptId);
                        break;

                    case "copyPath":
                        CopyPathRequested?.Invoke(this, message.Path);
                        break;

                    case "openSession":
                        OpenSessionRequested?.Invoke(this, message.SessionId);
                        break;

                    case "searchSessions":
                        SearchSessionsRequested?.Invoke(this, message.Query);
                        break;

                    case "syncSessions":
                        SyncSessionsRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "getSessionMessages":
                        GetSessionMessagesRequested?.Invoke(this, message.SessionId);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanelRenderer] Error handling message: {ex.Message}");
            }
        }

        private void SendMessage(string message)
        {
            var msgPreview = message.Length > 50 ? message.Substring(0, 50) + "..." : message;
            if (_webView?.CoreWebView2 != null && _isInitialized)
            {
                System.Diagnostics.Trace.WriteLine($"[ProjectPanelRenderer] SendMessage (immediate): {msgPreview}");
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
            else
            {
                System.Diagnostics.Trace.WriteLine($"[ProjectPanelRenderer] SendMessage (queued): {msgPreview}");
                _pendingMessage = message;
            }
        }

        /// <summary>
        /// Display project information.
        /// </summary>
        public void ShowProject(Project project, ProjectStats stats = null)
        {
            System.Diagnostics.Trace.WriteLine($"[ProjectPanelRenderer] ShowProject called: {project?.Name ?? "null"}, IsInitialized={_isInitialized}");
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"name\":\"{EscapeJson(project.Name ?? "")}\",");
            sb.Append($"\"path\":\"{EscapeJson(project.Path ?? "")}\",");
            sb.Append($"\"description\":\"{EscapeJson(project.Description ?? "")}\",");
            sb.Append($"\"changeLog\":\"{EscapeJson(project.ChangeLog ?? "")}\",");

            // Project info fields
            sb.Append($"\"projectType\":\"{EscapeJson(project.ProjectType ?? "")}\",");
            sb.Append($"\"currentVersion\":\"{EscapeJson(project.CurrentVersion ?? "")}\",");
            sb.Append($"\"icon\":\"{EscapeJson(project.Icon ?? "")}\",");
            sb.Append($"\"iconColor\":\"{EscapeJson(project.IconColor ?? "")}\",");

            // Build & deploy commands
            sb.Append($"\"buildCommand\":\"{EscapeJson(project.BuildCommand ?? "")}\",");
            sb.Append($"\"deployCommand\":\"{EscapeJson(project.DeployCommand ?? "")}\",");
            sb.Append($"\"launchCommand\":\"{EscapeJson(project.LaunchCommand ?? "")}\",");

            // Git info
            sb.Append($"\"gitRepoUrl\":\"{EscapeJson(project.GitRepoUrl ?? "")}\",");
            sb.Append($"\"gitDefaultBranch\":\"{EscapeJson(project.GitDefaultBranch ?? "")}\",");
            sb.Append($"\"gitAutoCommit\":{(project.GitAutoCommit ? "true" : "false")},");

            if (stats != null)
            {
                sb.Append("\"stats\":{");
                sb.Append($"\"fileCount\":{stats.FileCount},");
                sb.Append($"\"linesOfCode\":{stats.LinesOfCode},");
                sb.Append($"\"daysOld\":{stats.DaysOld}");
                sb.Append("},");
            }

            // Add prompts
            sb.Append("\"prompts\":[");
            if (project.Prompts != null)
            {
                for (int i = 0; i < project.Prompts.Count; i++)
                {
                    var p = project.Prompts[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"id\":\"{EscapeJson(p.Id ?? "")}\",");
                    sb.Append($"\"description\":\"{EscapeJson(p.Description ?? "")}\",");
                    sb.Append($"\"category\":\"{EscapeJson(p.Category ?? "")}\",");
                    sb.Append($"\"text\":\"{EscapeJson(p.Text ?? "")}\",");
                    sb.Append($"\"isGlobal\":{(p.IsGlobal ? "true" : "false")}");
                    sb.Append("}");
                }
            }
            sb.Append("]");

            sb.Append("}");

            SendMessage($"project:{sb}");
        }

        /// <summary>
        /// Display welcome state.
        /// </summary>
        public void ShowWelcome()
        {
            SendMessage("welcome:");
        }

        /// <summary>
        /// Display not registered state (Claude detected).
        /// </summary>
        public void ShowNotRegistered(string path)
        {
            SendMessage($"notRegistered:{path ?? ""}");
        }

        /// <summary>
        /// Display a list of sessions in the panel.
        /// </summary>
        public void ShowSessions(List<SessionSummary> sessions)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            if (sessions != null)
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"sessionId\":\"{EscapeJson(s.SessionId ?? "")}\",");
                    sb.Append($"\"summary\":\"{EscapeJson(s.Summary ?? "")}\",");
                    sb.Append($"\"firstPrompt\":\"{EscapeJson(s.FirstPrompt ?? "")}\",");
                    sb.Append($"\"messageCount\":{s.MessageCount},");
                    sb.Append($"\"created\":\"{s.Created:O}\",");
                    sb.Append($"\"modified\":\"{s.Modified:O}\",");
                    sb.Append($"\"gitBranch\":\"{EscapeJson(s.GitBranch ?? "")}\"");
                    sb.Append("}");
                }
            }
            sb.Append("]");

            SendMessage($"sessions:{sb}");
        }

        /// <summary>
        /// Clear the sessions display.
        /// </summary>
        public void ClearSessions()
        {
            SendMessage("sessions:[]");
        }

        /// <summary>
        /// Show the result of a sync operation.
        /// </summary>
        /// <param name="count">Number of sessions synced.</param>
        public void ShowSyncResult(int count)
        {
            SendMessage($"syncResult:{count}");
        }

        /// <summary>
        /// Show session messages in the expanded session card.
        /// </summary>
        /// <param name="sessionId">The session ID.</param>
        /// <param name="messages">List of messages to display.</param>
        public void ShowSessionMessages(string sessionId, List<SessionMessageSummary> messages)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"sessionId\":\"{EscapeJson(sessionId ?? "")}\",");
            sb.Append("\"messages\":[");

            if (messages != null)
            {
                for (int i = 0; i < messages.Count; i++)
                {
                    var m = messages[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"role\":\"{EscapeJson(m.Role ?? "")}\",");
                    sb.Append($"\"content\":\"{EscapeJson(m.Content ?? "")}\"");
                    sb.Append("}");
                }
            }

            sb.Append("]}");
            SendMessage($"sessionMessages:{sb}");
        }

        /// <summary>
        /// Show search results for message content search.
        /// </summary>
        /// <param name="results">List of search results.</param>
        /// <param name="query">The search query (for highlighting).</param>
        public void ShowSearchResults(List<MessageSearchResult> results, string query)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"query\":\"{EscapeJson(query ?? "")}\",");
            sb.Append("\"results\":[");

            if (results != null)
            {
                for (int i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"sessionId\":\"{EscapeJson(r.SessionId ?? "")}\",");
                    sb.Append($"\"sessionSummary\":\"{EscapeJson(r.SessionSummary ?? "")}\",");
                    sb.Append($"\"messageId\":{r.MessageId},");
                    sb.Append($"\"role\":\"{EscapeJson(r.Role ?? "")}\",");
                    sb.Append($"\"snippet\":\"{EscapeJson(r.ContentSnippet ?? "")}\",");
                    sb.Append($"\"timestamp\":\"{r.Timestamp:yyyy-MM-ddTHH:mm:ss}\"");
                    sb.Append("}");
                }
            }

            sb.Append("]}");
            SendMessage($"searchResults:{sb}");
        }

        /// <summary>
        /// Set the theme.
        /// </summary>
        public void SetTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(243, 243, 243);
            SendMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        /// <summary>
        /// Sets the font size for the project panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            size = Math.Max(8f, Math.Min(14f, size));
            SendMessage($"fontSize:{size}");
        }

        private string EscapeJson(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private MessageData ParseJsonMessage(string json)
        {
            var result = new MessageData();

            // Simple JSON parsing for our specific format
            json = json.Trim();
            if (json.StartsWith("{") && json.EndsWith("}"))
            {
                json = json.Substring(1, json.Length - 2);

                foreach (var pair in json.Split(','))
                {
                    var colonIdx = pair.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var key = pair.Substring(0, colonIdx).Trim().Trim('"');
                        var value = pair.Substring(colonIdx + 1).Trim().Trim('"');

                        switch (key)
                        {
                            case "type": result.Type = value; break;
                            case "promptId": result.PromptId = value; break;
                            case "path": result.Path = value; break;
                            case "sessionId": result.SessionId = value; break;
                            case "query": result.Query = value; break;
                        }
                    }
                }
            }

            return result;
        }

        private class MessageData
        {
            public string Type { get; set; }
            public string PromptId { get; set; }
            public string Path { get; set; }
            public string SessionId { get; set; }
            public string Query { get; set; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null)
                {
                    _webView.CoreWebView2?.Stop();
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Statistics about a project.
    /// </summary>
    public class ProjectStats
    {
        public int FileCount { get; set; }
        public int LinesOfCode { get; set; }
        public int DaysOld { get; set; }
    }

    /// <summary>
    /// Summary information about a Claude Code session.
    /// </summary>
    public class SessionSummary
    {
        public string SessionId { get; set; }
        public string Summary { get; set; }
        public string FirstPrompt { get; set; }
        public int MessageCount { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string GitBranch { get; set; }
    }

    /// <summary>
    /// Summary of a single message in a session.
    /// </summary>
    public class SessionMessageSummary
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
