using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.ActivityPanel
{
    /// <summary>
    /// WebView2-based renderer for the Activity Panel - "Mission Control" UI.
    /// Displays team status, activity feed, pinned intel, and metrics.
    /// </summary>
    public class ActivityPanelRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private readonly Queue<string> _pendingMessages = new();
        private bool _isDarkTheme = true;
        private DateTime _lastUpdateTime = DateTime.MinValue;
        private readonly TimeSpan _throttleInterval = TimeSpan.FromMilliseconds(500);
        private TerminalActivity _pendingActivityUpdate;
        private System.Windows.Forms.Timer _throttleTimer;
        private string _lastTeamDataHash = null; // Track last sent team data to avoid redundant full refreshes

        /// <summary>
        /// Event fired when WebView2 is ready.
        /// </summary>
        public event EventHandler Ready;

        /// <summary>
        /// Event fired when user clicks to focus on a team member.
        /// </summary>
        public event EventHandler<string> FocusMemberRequested;

        /// <summary>
        /// Event fired when user pins/unpins an activity item.
        /// </summary>
        public event EventHandler<PinEventArgs> PinToggled;

        /// <summary>
        /// Event fired when user clicks to copy text.
        /// </summary>
        public event EventHandler<string> CopyRequested;

        /// <summary>Event raised when user clicks the refresh button</summary>
        public event EventHandler RefreshRequested;

        /// <summary>
        /// Event fired when user clicks "Create Task" on an expanded feed item.
        /// Args: (title, description)
        /// </summary>
        public event EventHandler<TaskCreateRequestArgs> TaskCreateRequested;

        /// <summary>
        /// Gets whether the renderer is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Debug log sink, wired from the owning ActivityPanelDocument once its broker is available.
        /// </summary>
        public DebugLogService DebugLogService { get; set; }

        public ActivityPanelRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = Color.FromArgb(30, 30, 30);
            Name = "ActivityPanelRenderer";
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

            string path = Path.Combine(assemblyDir, "ActivityPanel", "panel.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "ActivityPanel", "panel.html");
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
                        DebugLogService?.Info("ActivityPanel", $"JS 'ready' received! Pending messages: {_pendingMessages.Count}");
                        _isInitialized = true;
                        _isInitializing = false;
                        SendMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");
                        // Flush all pending messages in order
                        while (_pendingMessages.Count > 0)
                        {
                            var pendingMsg = _pendingMessages.Dequeue();
                            DebugLogService?.Trace("ActivityPanel", $"Flushing queued message: {pendingMsg.Substring(0, Math.Min(60, pendingMsg.Length))}...");
                            _webView.CoreWebView2.PostWebMessageAsString(pendingMsg);
                        }
                        DebugLogService?.Info("ActivityPanel", "Firing Ready event");
                        Ready?.Invoke(this, EventArgs.Empty);
                        break;

                    case "focusMember":
                        FocusMemberRequested?.Invoke(this, message.MemberName);
                        break;

                    case "pinToggle":
                        PinToggled?.Invoke(this, new PinEventArgs(message.ItemId, message.IsPinned));
                        break;

                    case "copy":
                        CopyRequested?.Invoke(this, message.Text);
                        if (!string.IsNullOrEmpty(message.Text))
                        {
                            try { Clipboard.SetText(message.Text); }
                            catch { }
                        }
                        break;

                    case "refresh":
                        DebugLogService?.Info("ActivityPanel", "Manual refresh requested");
                        RefreshRequested?.Invoke(this, EventArgs.Empty);
                        break;

                    case "createTask":
                        var title = ExtractJsonString(json, "title");
                        var description = ExtractJsonString(json, "description");
                        DebugLogService?.Info("ActivityPanel", $"Create task requested: {title}");
                        TaskCreateRequested?.Invoke(this, new TaskCreateRequestArgs(title, description));
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugLogService?.Error("ActivityPanel", $"Error handling message: {ex.Message}");
            }
        }

        private void SendMessage(string message)
        {
            if (_webView?.CoreWebView2 != null && _isInitialized)
            {
                DebugLogService?.Trace("ActivityPanel", $"SendMessage: Posting to WebView2 - {message.Substring(0, Math.Min(80, message.Length))}...");
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
            else
            {
                // Queue message to send when WebView2 is ready
                DebugLogService?.Trace("ActivityPanel", $"SendMessage: QUEUING (WebView2 not ready) - {message.Substring(0, Math.Min(80, message.Length))}...");
                _pendingMessages.Enqueue(message);
            }
        }

        /// <summary>
        /// Set the theme for the panel.
        /// </summary>
        public void SetTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            SendMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        /// <summary>
        /// Sets the font size for the activity panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            size = Math.Max(8f, Math.Min(14f, size));
            SendMessage($"fontSize:{size}");
        }

        /// <summary>
        /// Send team member profiles (including avatar URLs) to the JavaScript UI.
        /// </summary>
        public void ShowProfiles(List<TeamMemberProfile> profiles)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            if (profiles != null)
            {
                for (int i = 0; i < profiles.Count; i++)
                {
                    var p = profiles[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"id\":\"{EscapeJson(p.Id ?? "")}\",");
                    sb.Append($"\"displayName\":\"{EscapeJson(p.DisplayName ?? "")}\",");
                    sb.Append($"\"avatarUrl\":\"{EscapeJson(p.AvatarUrl ?? "")}\"");
                    sb.Append("}");
                }
            }
            sb.Append("]");

            SendMessage($"profiles:{sb}");
            DebugLogService?.Trace("ActivityPanel", $"Sent {profiles?.Count ?? 0} profiles to UI");
        }

        /// <summary>
        /// Display all team activity data.
        /// Only sends update if data has changed to avoid UI flashing from redundant full refreshes.
        /// </summary>
        public void ShowTeamActivity(List<TerminalActivity> activities)
        {
            DebugLogService?.Trace("ActivityPanel", $"ShowTeamActivity called with {activities?.Count ?? 0} activities, IsInitialized={_isInitialized}");

            var sb = new StringBuilder();
            sb.Append("[");
            if (activities != null)
            {
                for (int i = 0; i < activities.Count; i++)
                {
                    var a = activities[i];
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append($"\"terminal\":\"{EscapeJson(a.Terminal ?? "")}\",");
                    sb.Append($"\"status\":\"{EscapeJson(a.Status ?? "idle")}\",");
                    sb.Append($"\"activity\":\"{EscapeJson(a.Activity ?? "")}\",");
                    sb.Append($"\"blockedBy\":\"{EscapeJson(a.BlockedBy ?? "")}\",");
                    sb.Append($"\"taskId\":\"{EscapeJson(a.TaskId ?? "")}\",");
                    sb.Append($"\"planId\":\"{EscapeJson(a.PlanId ?? "")}\",");
                    sb.Append($"\"updatedAt\":\"{a.UpdatedAt:O}\"");
                    sb.Append("}");
                }
            }
            sb.Append("]");

            var jsonData = sb.ToString();

            // Create a hash of the data (excluding timestamps which change constantly)
            // to detect if anything meaningful has changed
            var hashBuilder = new StringBuilder();
            if (activities != null)
            {
                foreach (var a in activities.OrderBy(x => x.Terminal))
                {
                    hashBuilder.Append($"{a.Terminal}|{a.Status}|{a.Activity}|{a.BlockedBy}|{a.TaskId}|{a.PlanId};");
                }
            }
            var currentHash = hashBuilder.ToString();

            // Skip sending if nothing meaningful has changed - avoids UI flashing
            if (currentHash == _lastTeamDataHash)
            {
                DebugLogService?.Trace("ActivityPanel", "ShowTeamActivity: Data unchanged, skipping update");
                return;
            }
            _lastTeamDataHash = currentHash;

            var msg = $"team:{jsonData}";
            DebugLogService?.Trace("ActivityPanel", $"Sending team message ({msg.Length} chars)");
            SendMessage(msg);
        }

        /// <summary>
        /// Push a single activity update (throttled to 500ms, queues latest update instead of dropping).
        /// </summary>
        public void PushActivityUpdate(TerminalActivity activity)
        {
            var now = DateTime.UtcNow;
            if (now - _lastUpdateTime < _throttleInterval)
            {
                // Throttled - queue this update to send after throttle period
                _pendingActivityUpdate = activity;
                EnsureThrottleTimer();
                return;
            }
            _lastUpdateTime = now;
            _pendingActivityUpdate = null; // Clear pending since we're sending now

            SendActivityUpdateMessage(activity);
        }

        private void EnsureThrottleTimer()
        {
            if (_throttleTimer == null)
            {
                _throttleTimer = new System.Windows.Forms.Timer();
                _throttleTimer.Interval = (int)_throttleInterval.TotalMilliseconds;
                _throttleTimer.Tick += OnThrottleTimerTick;
            }
            if (!_throttleTimer.Enabled)
            {
                _throttleTimer.Start();
            }
        }

        private void OnThrottleTimerTick(object sender, EventArgs e)
        {
            _throttleTimer.Stop();

            // Send the pending update if there is one
            if (_pendingActivityUpdate != null)
            {
                var activity = _pendingActivityUpdate;
                _pendingActivityUpdate = null;
                _lastUpdateTime = DateTime.UtcNow;
                SendActivityUpdateMessage(activity);
            }
        }

        private void SendActivityUpdateMessage(TerminalActivity activity)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"terminal\":\"{EscapeJson(activity.Terminal ?? "")}\",");
            sb.Append($"\"status\":\"{EscapeJson(activity.Status ?? "idle")}\",");
            sb.Append($"\"activity\":\"{EscapeJson(activity.Activity ?? "")}\",");
            sb.Append($"\"blockedBy\":\"{EscapeJson(activity.BlockedBy ?? "")}\",");
            sb.Append($"\"taskId\":\"{EscapeJson(activity.TaskId ?? "")}\",");
            sb.Append($"\"planId\":\"{EscapeJson(activity.PlanId ?? "")}\",");
            sb.Append($"\"updatedAt\":\"{activity.UpdatedAt:O}\"");
            sb.Append("}");

            SendMessage($"activityUpdate:{sb}");
        }

        /// <summary>
        /// Update metrics display.
        /// </summary>
        public void ShowMetrics(ActivityMetrics metrics)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"totalTasks\":{metrics.TotalTasks},");
            sb.Append($"\"completedTasks\":{metrics.CompletedTasks},");
            sb.Append($"\"inProgressTasks\":{metrics.InProgressTasks},");
            sb.Append($"\"pendingTasks\":{metrics.PendingTasks},");
            sb.Append($"\"messageCount\":{metrics.MessageCount},");
            sb.Append($"\"learnedCount\":{metrics.LearnedCount}");
            if (metrics.ActivityData != null && metrics.ActivityData.Length > 0)
            {
                sb.Append($",\"activityData\":[{string.Join(",", metrics.ActivityData)}]");
            }
            sb.Append("}");

            SendMessage($"metrics:{sb}");
        }

        /// <summary>
        /// Add an item to the activity feed.
        /// </summary>
        public void AddFeedItem(ActivityFeedItem item)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"\"id\":\"{EscapeJson(item.Id ?? "")}\",");
            sb.Append($"\"terminal\":\"{EscapeJson(item.Terminal ?? "")}\",");
            sb.Append($"\"type\":\"{EscapeJson(item.Type ?? "")}\",");
            sb.Append($"\"content\":\"{EscapeJson(item.Content ?? "")}\",");
            sb.Append($"\"timestamp\":\"{item.Timestamp:O}\",");
            sb.Append($"\"isPinned\":{(item.IsPinned ? "true" : "false")},");
            sb.Append($"\"isNew\":true");
            if (!string.IsNullOrEmpty(item.Details))
                sb.Append($",\"details\":\"{EscapeJson(item.Details)}\"");
            if (!string.IsNullOrEmpty(item.Severity))
                sb.Append($",\"severity\":\"{EscapeJson(item.Severity)}\"");
            if (!string.IsNullOrEmpty(item.ActivityType))
                sb.Append($",\"activityType\":\"{EscapeJson(item.ActivityType)}\"");
            sb.Append("}");

            SendMessage($"feedItem:{sb}");
        }

        /// <summary>
        /// Send task creation confirmation back to JavaScript.
        /// </summary>
        public void SendTaskCreatedConfirmation(string taskId)
        {
            SendMessage($"taskCreated:{taskId}");
        }

        /// <summary>
        /// Send task creation failure back to JavaScript.
        /// </summary>
        public void SendTaskCreateFailed(string error)
        {
            SendMessage($"taskCreateFailed:{EscapeJson(error ?? "Unknown error")}");
        }

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
                MemberName = ExtractJsonString(json, "memberName"),
                ItemId = ExtractJsonString(json, "itemId"),
                IsPinned = ExtractJsonBool(json, "isPinned"),
                Text = ExtractJsonString(json, "text")
            };
        }

        private string ExtractJsonString(string json, string key)
        {
            var pattern = $"\"{key}\":\"";
            int start = json.IndexOf(pattern);
            if (start < 0) return "";
            start += pattern.Length;
            int end = start;
            while (end < json.Length)
            {
                end = json.IndexOf('"', end);
                if (end < 0) return "";
                if (end > 0 && json[end - 1] == '\\') { end++; continue; }
                break;
            }
            return json.Substring(start, end - start)
                       .Replace("\\\"", "\"")
                       .Replace("\\\\", "\\")
                       .Replace("\\n", "\n")
                       .Replace("\\r", "\r")
                       .Replace("\\t", "\t");
        }

        private bool ExtractJsonBool(string json, string key)
        {
            var pattern = $"\"{key}\":";
            int start = json.IndexOf(pattern);
            if (start < 0) return false;
            start += pattern.Length;
            return json.Substring(start).TrimStart().StartsWith("true");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_throttleTimer != null)
                {
                    _throttleTimer.Stop();
                    _throttleTimer.Tick -= OnThrottleTimerTick;
                    _throttleTimer.Dispose();
                    _throttleTimer = null;
                }
                _webView?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Event args for task creation requests from the activity panel.
    /// </summary>
    public class TaskCreateRequestArgs : EventArgs
    {
        public string Title { get; }
        public string Description { get; }

        public TaskCreateRequestArgs(string title, string description)
        {
            Title = title;
            Description = description;
        }
    }

    /// <summary>
    /// Event args for pin toggle events.
    /// </summary>
    public class PinEventArgs : EventArgs
    {
        public string ItemId { get; }
        public bool IsPinned { get; }

        public PinEventArgs(string itemId, bool isPinned)
        {
            ItemId = itemId;
            IsPinned = isPinned;
        }
    }

    /// <summary>
    /// Metrics for the activity panel.
    /// </summary>
    public class ActivityMetrics
    {
        public int TotalTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int InProgressTasks { get; set; }
        public int PendingTasks { get; set; }
        public int MessageCount { get; set; }
        public int LearnedCount { get; set; }
        public int[] ActivityData { get; set; }
    }

    /// <summary>
    /// Item for the activity feed.
    /// </summary>
    public class ActivityFeedItem
    {
        public string Id { get; set; }
        public string Terminal { get; set; }
        public string Type { get; set; } // "learned", "chat", "task", "decision"
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsPinned { get; set; }
        public string Details { get; set; }
        public string Severity { get; set; } // "info", "warning", "error"
        public string ActivityType { get; set; } // "PLAN_CREATED", "BUILD_FAILED", etc.
    }
}
