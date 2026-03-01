using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
        /// Event fired when JS requests a project field update.
        /// Sender args: (fieldName, newValue)
        /// </summary>
        public event EventHandler<FieldUpdateEventArgs> FieldUpdateRequested;

        /// <summary>
        /// Event fired when JS requests an association add/update/delete.
        /// </summary>
        public event EventHandler<AssociationUpdateEventArgs> AssociationUpdateRequested;

        /// <summary>
        /// Event fired when JS requests a full association refresh (after add/delete).
        /// </summary>
        public event EventHandler RefreshAssociationsRequested;

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

                    case "updateField":
                        FieldUpdateRequested?.Invoke(this, new FieldUpdateEventArgs(message.Field, message.Value));
                        break;

                    case "updateAssociation":
                        AssociationUpdateRequested?.Invoke(this, new AssociationUpdateEventArgs(
                            message.TableName, message.Action, message.ItemJson));
                        break;

                    case "getAssociations":
                        RefreshAssociationsRequested?.Invoke(this, EventArgs.Empty);
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
        /// Sends "project:{json}" to the WebView2 panel with all 23 project fields.
        /// </summary>
        public void ShowProject(Project project, ProjectStats stats = null)
        {
            System.Diagnostics.Trace.WriteLine($"[ProjectPanelRenderer] ShowProject called: {project?.Name ?? "null"}, IsInitialized={_isInitialized}");
            var sb = new StringBuilder();
            sb.Append("{");

            // Core identity
            sb.Append($"\"name\":\"{EscapeJson(project.Name ?? "")}\",");
            sb.Append($"\"path\":\"{EscapeJson(project.Path ?? "")}\",");
            sb.Append($"\"description\":\"{EscapeJson(project.Description ?? "")}\",");
            sb.Append($"\"changeLog\":\"{EscapeJson(project.ChangeLog ?? "")}\",");

            // Project info fields
            sb.Append($"\"projectType\":\"{EscapeJson(project.ProjectType ?? "")}\",");
            sb.Append($"\"currentVersion\":\"{EscapeJson(project.CurrentVersion ?? "")}\",");
            sb.Append($"\"icon\":\"{EscapeJson(project.Icon ?? "")}\",");
            sb.Append($"\"iconColor\":\"{EscapeJson(project.IconColor ?? "")}\",");

            // Paths (new fields)
            sb.Append($"\"sourcePath\":\"{EscapeJson(project.SourcePath ?? "")}\",");
            sb.Append($"\"deployPath\":\"{EscapeJson(project.DeployPath ?? "")}\",");
            sb.Append($"\"buildOutputPath\":\"{EscapeJson(project.BuildOutputPath ?? "")}\",");

            // Build & deploy commands
            sb.Append($"\"buildCommand\":\"{EscapeJson(project.BuildCommand ?? "")}\",");
            sb.Append($"\"deployCommand\":\"{EscapeJson(project.DeployCommand ?? "")}\",");
            sb.Append($"\"launchCommand\":\"{EscapeJson(project.LaunchCommand ?? "")}\",");

            // Git info
            sb.Append($"\"gitRepoUrl\":\"{EscapeJson(project.GitRepoUrl ?? "")}\",");
            sb.Append($"\"gitDefaultBranch\":\"{EscapeJson(project.GitDefaultBranch ?? "")}\",");
            sb.Append($"\"gitAutoCommit\":{(project.GitAutoCommit ? "true" : "false")},");

            // Team lead
            sb.Append($"\"teamLead\":\"{EscapeJson(project.TeamLead ?? "")}\",");

            // Status / flags (new fields)
            sb.Append($"\"isPinned\":{(project.IsPinned ? "true" : "false")},");
            sb.Append($"\"createdBy\":\"{EscapeJson(project.CreatedBy ?? "")}\",");

            // Timestamps (new fields) — ISO 8601 so JS can parse them
            sb.Append($"\"createdAt\":\"{EscapeJson(project.CreatedAt == default ? "" : project.CreatedAt.ToString("O"))}\",");
            sb.Append($"\"updatedAt\":\"{EscapeJson(project.UpdatedAt == default ? "" : project.UpdatedAt.ToString("O"))}\",");
            sb.Append($"\"lastOpenedAt\":\"{EscapeJson(project.LastOpenedAt == default ? "" : project.LastOpenedAt.ToString("O"))}\",");

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
        /// Send all association data (agents, MCP servers, specialist agents, paths, prompts, skills)
        /// to the WebView2 panel. Sends "associations:{json}".
        /// </summary>
        public void ShowAssociations(ProjectContext context)
        {
            if (context == null)
            {
                SendMessage("associations:{}");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("{");

            // Agents
            sb.Append("\"agents\":[");
            for (int i = 0; i < context.Agents.Count; i++)
            {
                var a = context.Agents[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":{a.Id},");
                sb.Append($"\"agentName\":\"{EscapeJson(a.AgentName ?? "")}\",");
                sb.Append($"\"role\":\"{EscapeJson(a.Role ?? "")}\",");
                sb.Append($"\"preferredModel\":\"{EscapeJson(a.PreferredModel ?? "")}\"");
                sb.Append("}");
            }
            sb.Append("],");

            // MCP servers
            sb.Append("\"mcpServers\":[");
            for (int i = 0; i < context.McpServers.Count; i++)
            {
                var m = context.McpServers[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":{m.Id},");
                sb.Append($"\"serverName\":\"{EscapeJson(m.ServerName ?? "")}\",");
                sb.Append($"\"isEnabled\":{(m.IsEnabled ? "true" : "false")}");
                sb.Append("}");
            }
            sb.Append("],");

            // Specialist agents
            sb.Append("\"specialistAgents\":[");
            for (int i = 0; i < context.SpecialistAgents.Count; i++)
            {
                var s = context.SpecialistAgents[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":{s.Id},");
                sb.Append($"\"agentType\":\"{EscapeJson(s.AgentType ?? "")}\",");
                sb.Append($"\"isEnabled\":{(s.IsEnabled ? "true" : "false")},");
                sb.Append($"\"customPrompt\":\"{EscapeJson(s.CustomPrompt ?? "")}\"");
                sb.Append("}");
            }
            sb.Append("],");

            // Paths
            sb.Append("\"projectPaths\":[");
            for (int i = 0; i < context.Paths.Count; i++)
            {
                var p = context.Paths[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":{p.Id},");
                sb.Append($"\"pathType\":\"{EscapeJson(p.PathType ?? "")}\",");
                sb.Append($"\"pathValue\":\"{EscapeJson(p.PathValue ?? "")}\",");
                sb.Append($"\"description\":\"{EscapeJson(p.Description ?? "")}\"");
                sb.Append("}");
            }
            sb.Append("],");

            // Prompts (SQLite project_prompts, not legacy JSON Prompts)
            sb.Append("\"dbPrompts\":[");
            for (int i = 0; i < context.Prompts.Count; i++)
            {
                var pr = context.Prompts[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":{pr.Id},");
                sb.Append($"\"promptType\":\"{EscapeJson(pr.PromptType ?? "")}\",");
                sb.Append($"\"promptText\":\"{EscapeJson(pr.PromptText ?? "")}\",");
                sb.Append($"\"displayOrder\":{pr.DisplayOrder}");
                sb.Append("}");
            }
            sb.Append("],");

            // Skills
            sb.Append("\"skills\":[");
            for (int i = 0; i < context.Skills.Count; i++)
            {
                var sk = context.Skills[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":{sk.Id},");
                sb.Append($"\"skillName\":\"{EscapeJson(sk.SkillName ?? "")}\",");
                sb.Append($"\"isEnabled\":{(sk.IsEnabled ? "true" : "false")}");
                sb.Append("}");
            }
            sb.Append("]");

            sb.Append("}");
            SendMessage($"associations:{sb}");
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

        /// <summary>
        /// Notify JS that a field save succeeded or failed.
        /// Sends "fieldSaved:{\"field\":\"name\",\"success\":true}"
        /// </summary>
        public void SendFieldSaved(string field, bool success)
        {
            SendMessage($"fieldSaved:{{\"field\":\"{EscapeJson(field ?? "")}\",\"success\":{(success ? "true" : "false")}}}");
        }

        /// <summary>
        /// Send team lead profile options to the WebView2 dropdown.
        /// Sends "teamLeadOptions:[{\"id\":\"Alice\",\"displayName\":\"Alice\"},...]"
        /// </summary>
        public void SendTeamLeadOptions(List<(string Id, string DisplayName, string AvatarUrl)> options)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                if (i > 0) sb.Append(",");
                sb.Append("{");
                sb.Append($"\"id\":\"{EscapeJson(o.Id ?? "")}\",");
                sb.Append($"\"displayName\":\"{EscapeJson(o.DisplayName ?? "")}\",");
                sb.Append($"\"avatarUrl\":\"{EscapeJson(o.AvatarUrl ?? "")}\"");
                sb.Append("}");
            }
            sb.Append("]");
            SendMessage($"teamLeadOptions:{sb}");
        }

        /// <summary>
        /// Notify JS that an association operation succeeded or failed.
        /// Sends "associationSaved:{\"tableName\":\"agents\",\"action\":\"add\",\"success\":true}"
        /// </summary>
        public void SendAssociationSaved(string tableName, string action, bool success, int newId = 0)
        {
            var idPart = newId > 0 ? $",\"newId\":{newId}" : "";
            SendMessage($"associationSaved:{{\"tableName\":\"{EscapeJson(tableName ?? "")}\",\"action\":\"{EscapeJson(action ?? "")}\",\"success\":{(success ? "true" : "false")}{idPart}}}");
        }

        /// <summary>
        /// Ask the panel to show a hint for creating a new project via /new-project.
        /// </summary>
        public void ShowNewProjectHint()
        {
            SendMessage("newProjectHint:");
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

            json = json?.Trim();
            if (string.IsNullOrEmpty(json) || !json.StartsWith("{"))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeEl))
                    result.Type = typeEl.GetString();

                if (root.TryGetProperty("promptId", out var promptIdEl))
                    result.PromptId = promptIdEl.GetString();

                if (root.TryGetProperty("path", out var pathEl))
                    result.Path = pathEl.GetString();

                if (root.TryGetProperty("sessionId", out var sessionIdEl))
                    result.SessionId = sessionIdEl.GetString();

                if (root.TryGetProperty("query", out var queryEl))
                    result.Query = queryEl.GetString();

                // Fields used by editing actions
                if (root.TryGetProperty("field", out var fieldEl))
                    result.Field = fieldEl.GetString();

                if (root.TryGetProperty("value", out var valueEl))
                    result.Value = valueEl.ValueKind == JsonValueKind.String
                        ? valueEl.GetString()
                        : valueEl.GetRawText();

                if (root.TryGetProperty("tableName", out var tableEl))
                    result.TableName = tableEl.GetString();

                if (root.TryGetProperty("action", out var actionEl))
                    result.Action = actionEl.GetString();

                if (root.TryGetProperty("item", out var itemEl))
                    result.ItemJson = itemEl.GetRawText();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectPanelRenderer] ParseJsonMessage failed: {ex.Message}");
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
            // Editing fields
            public string Field { get; set; }
            public string Value { get; set; }
            public string TableName { get; set; }
            public string Action { get; set; }
            public string ItemJson { get; set; }
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

    /// <summary>
    /// Event args for a JS "updateField" message (single field in-place edit).
    /// </summary>
    public class FieldUpdateEventArgs : EventArgs
    {
        public string Field { get; }
        public string Value { get; }

        public FieldUpdateEventArgs(string field, string value)
        {
            Field = field;
            Value = value;
        }
    }

    /// <summary>
    /// Event args for a JS "updateAssociation" message (add/update/delete on an association table).
    /// </summary>
    public class AssociationUpdateEventArgs : EventArgs
    {
        public string TableName { get; }
        public string Action { get; }
        public string ItemJson { get; }

        public AssociationUpdateEventArgs(string tableName, string action, string itemJson)
        {
            TableName = tableName;
            Action = action;
            ItemJson = itemJson;
        }
    }
}
