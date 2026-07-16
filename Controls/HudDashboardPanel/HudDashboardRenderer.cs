using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based dashboard panel showing the active task (hero card that
    /// opens the Lifecycle board on click), project info, task stats, git
    /// status, last session summary, and recent activity feed.
    /// Lives as a permanent tab in HudTabContainer.
    /// </summary>
    public class HudDashboardRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        private MessageBroker _broker;
        private string _projectPath;
        private string _projectId;

        // Queue messages sent before WebView2 is ready
        private readonly Queue<string> _pendingMessages = new();

        // Debounce timer for activity refresh — batches rapid events and
        // gives the DB write time to complete before we read back.
        private System.Windows.Forms.Timer _activityDebounce;

        /// <summary>
        /// Raised when the WebView2 zoom factor changes (e.g. Ctrl+wheel).
        /// </summary>
        public event EventHandler<double> ZoomChanged;

        /// <summary>
        /// Raised when the user clicks the dirty-state changes badge in the dashboard
        /// widget. Subscribers (typically TerminalDocument) deep-link to the project's
        /// HUD Git tab so the dashboard widget acts as a glance summary that escalates
        /// to the full Git tab on demand.
        /// </summary>
        public event EventHandler OpenGitTabRequested;

        /// <summary>
        /// Raised when the user clicks a file name in the activity feed to view its diff.
        /// The string argument is the file name (basename) from the activity entry.
        /// </summary>
        public event EventHandler<string> ShowDiffRequested;

        public HudDashboardRenderer()
        {
            SuspendLayout();

            BackColor = System.Drawing.Color.FromArgb(26, 26, 46);
            Name = "HudDashboardRenderer";
            Visible = false;

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "dashboardWebView"
            };

            Controls.Add(_webView);
            ResumeLayout(false);

            VisibleChanged += OnVisibleChanged;
        }

        private void OnVisibleChanged(object sender, EventArgs e)
        {
            if (Visible && !_isInitialized && !_isInitializing)
            {
                InitializeWebView();
            }
        }

        private async void InitializeWebView()
        {
            if (_isInitializing || _isInitialized) return;
            _isInitializing = true;

            try
            {
                var environment = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(environment);

                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? System.Drawing.Color.FromArgb(26, 26, 46)
                    : System.Drawing.Color.FromArgb(245, 245, 245);

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                string htmlPath = GetHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    _isInitializing = false;
                }
            }
            catch
            {
                _isInitializing = false;
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string path = Path.Combine(assemblyDir, "Controls", "HudDashboardPanel", "hud-dashboard.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "HudDashboardPanel", "hud-dashboard.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "Controls", "HudDashboardPanel", "hud-dashboard.html");
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string json = e.WebMessageAsJson;
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                var msgType = typeEl.GetString();

                if (msgType == "ready")
                {
                    _isInitialized = true;
                    _isInitializing = false;

                    PostJsonMessage(new { type = "theme", isDark = _isDarkTheme });

                    // Flush pending messages
                    while (_pendingMessages.Count > 0)
                    {
                        PostRawJson(_pendingMessages.Dequeue());
                    }

                    _webView.ZoomFactorChanged += (s, ev) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                    if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                        _webView.ZoomFactor = _pendingZoom;

                    // Initial data push
                    RefreshAll();
                }
                else if (msgType == "showDiff")
                {
                    if (root.TryGetProperty("fileName", out var fileNameEl))
                    {
                        var fileName = fileNameEl.GetString();
                        if (!string.IsNullOrEmpty(fileName))
                            ShowDiffRequested?.Invoke(this, fileName);
                    }
                }
                else if (msgType == "showGitTab")
                {
                    OpenGitTabRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch { }
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Wires the dashboard to the broker and registers event subscriptions.
        /// </summary>
        public void Initialize(MessageBroker broker)
        {
            _broker = broker;
            if (_broker == null) return;

            _broker.TasksUpdated -= OnTasksUpdated;
            _broker.TasksUpdated += OnTasksUpdated;

            _broker.ProjectsUpdated -= OnProjectsUpdated;
            _broker.ProjectsUpdated += OnProjectsUpdated;

            // Subscribe to the broker-level ActivityRecorded event — this fires
            // for ALL activities regardless of whether ActivityFeedService is set.
            // (Previously we subscribed to ActivityFeedService.ActivityRecorded which
            // could be null at init time, causing the dashboard to miss live updates.)
            _broker.ActivityRecorded -= OnBrokerActivityRecorded;
            _broker.ActivityRecorded += OnBrokerActivityRecorded;

            // Set up debounce timer — fires 250ms after the last activity event,
            // giving the DB write time to complete before we read back.
            if (_activityDebounce == null)
            {
                _activityDebounce = new System.Windows.Forms.Timer { Interval = 250 };
                _activityDebounce.Tick += (s, e) =>
                {
                    _activityDebounce.Stop();
                    RefreshActivity();
                };
            }

        }

        /// <summary>
        /// Sets the project context for this dashboard.
        /// </summary>
        public void SetProject(string projectId, string projectPath, string projectName)
        {
            _projectId = projectId;
            _projectPath = projectPath;

            if (_isInitialized)
            {
                RefreshAll();
            }
        }

        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized)
                PostJsonMessage(new { type = "theme", isDark = _isDarkTheme });
        }

        public void SetZoomFactor(double zoom)
        {
            _pendingZoom = zoom;
            if (_webView?.CoreWebView2 != null)
                _webView.ZoomFactor = zoom;
        }

        // -------------------------------------------------------------------------
        // Data refresh
        // -------------------------------------------------------------------------

        /// <summary>
        /// Refreshes all dashboard data: project info, task stats, activity.
        /// </summary>
        public void RefreshAll()
        {
            if (_broker == null) return;

            RefreshProjectInfo();
            RefreshTaskStats();
            RefreshActivity();
        }

        private async void RefreshProjectInfo()
        {
            if (string.IsNullOrEmpty(_projectPath))
            {
                SendMessage(new { type = "no_project" });
                return;
            }

            // Gather project info (non-blocking)
            string projectName = "";
            string sessionSummary = "No session data available";

            if (!string.IsNullOrEmpty(_projectId))
            {
                var projectResult = _broker.GetProject(_projectId);
                if (projectResult?.Success == true)
                {
                    projectName = projectResult.Project?.Name ?? "";
                }
            }

            if (string.IsNullOrEmpty(projectName) && !string.IsNullOrEmpty(_projectPath))
            {
                projectName = Path.GetFileName(_projectPath.TrimEnd('\\', '/'));
            }

            // Git info — read through the shared GitRepoService so this dashboard
            // widget and the HUD Git tab use one source of truth. Runs on a
            // background thread to keep LibGit2Sharp work off the UI.
            string branch = "";
            int changes = 0;
            string lastCommitMsg = "";
            string lastCommitHash = "";
            string lastCommitTime = "";

            try
            {
                var gitSvc = _broker?.GitRepos?.GetOrCreate(_projectPath);
                if (gitSvc != null)
                {
                    var gitData = await System.Threading.Tasks.Task.Run(() =>
                    {
                        string b = gitSvc.CurrentBranch ?? "";
                        var status = gitSvc.GetWorkingTreeStatus();
                        int c = status?.Count ?? 0;

                        var commits = gitSvc.GetRecentCommits(1);
                        string hash = "", msg = "", time = "";
                        if (commits != null && commits.Count > 0)
                        {
                            var head = commits[0];
                            hash = head.FullSha ?? "";
                            msg = head.Subject ?? "";
                            // Format matches git's %aI exactly (no fractional seconds) so the
                            // wire format is byte-equivalent to the previous RunGitCommand path
                            // and existing JS that parses by string (rather than Date.parse) keeps working.
                            time = head.When != DateTimeOffset.MinValue
                                ? head.When.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)
                                : "";
                        }
                        return new { branch = b, changes = c, hash, msg, time };
                    });

                    branch = gitData.branch;
                    changes = gitData.changes;
                    lastCommitHash = gitData.hash;
                    lastCommitMsg = gitData.msg;
                    lastCommitTime = gitData.time;
                }
            }
            catch { }

            // Latest session summary
            try
            {
                var sessionLineageService = _broker.SessionLineageService;
                if (sessionLineageService != null && !string.IsNullOrEmpty(_projectPath))
                {
                    var latestSession = sessionLineageService.GetMostRecentSessionForProject(_projectPath);
                    if (latestSession != null && !string.IsNullOrEmpty(latestSession.Summary))
                    {
                        sessionSummary = latestSession.Summary;
                    }
                }
            }
            catch { }

            SendMessage(new
            {
                type = "project_info",
                name = projectName,
                path = _projectPath,
                branch,
                changes,
                lastCommitMsg,
                lastCommitHash,
                lastCommitTime,
                sessionSummary
            });
        }

        private void RefreshTaskStats()
        {
            var tasks = _broker.GetTasks(_projectId);
            if (tasks == null) return;

            int todo = tasks.Count(t => t.Status == "todo");
            int inProgress = tasks.Count(t => t.Status == "in_progress");
            int done = tasks.Count(t => t.Status == "done");
            int suggestions = tasks.Count(t => t.Status == "suggestion");

            SendMessage(new
            {
                type = "task_stats",
                todo,
                inProgress,
                done,
                suggestions
            });
        }

        private void RefreshActivity()
        {
            var feedService = _broker.ActivityFeedService;
            if (feedService == null) return;

            // No project bound → show the empty state. GetRecentActivities with a null
            // projectId applies no WHERE clause, and a global all-projects feed under a
            // "Recent Project Activity" header would be misleading (task e8c6b52f).
            if (string.IsNullOrEmpty(_projectId))
            {
                SendMessage(new { type = "activity", items = Array.Empty<object>() });
                return;
            }

            // Fetch activities filtered by the current project
            var recentActivities = feedService.GetRecentActivities(60, projectId: _projectId);
            if (recentActivities == null || recentActivities.Count == 0)
            {
                SendMessage(new { type = "activity", items = Array.Empty<object>() });
                return;
            }

            // Filter out TOOL_START entries — TOOL_COMPLETE captures the same info,
            // and showing both creates duplicates in the feed.
            var items = recentActivities
                .Where(a => a.ActivityType != "TOOL_START")
                .Take(30)
                .Select(a => new
                {
                    agent = a.Actor,
                    text = a.Summary,
                    timestamp = a.Timestamp.ToString("o"),
                    icon = GetActivityIcon(a.ActivityType),
                    // type + severity let the dashboard JS group consecutive same-kind
                    // entries and tier them by importance (surface lifecycle/build/errors,
                    // demote housekeeping like the 5-min worktree_janitor_sweep). The raw
                    // feed is untouched — the Activity panel still shows every row.
                    type = a.ActivityType,
                    severity = a.Severity
                }).ToArray();

            SendMessage(new { type = "activity", items });
        }

        private string GetActivityIcon(string activityType)
        {
            if (string.IsNullOrEmpty(activityType)) return "\u26a1";

            // Activity types persist as the composite "{Type}_{Action}" (MessageBroker.RecordActivity),
            // so the worktree janitor arrives as "worktree_janitor_sweep" \u2014 not the bare "janitor_sweep"
            // the old switch expected, which meant every janitor row fell through to the default icon.
            // Match on the substring so any janitor/worktree-maintenance variant gets the broom.
            if (activityType.Contains("janitor", StringComparison.OrdinalIgnoreCase)) return "\ud83e\uddf9"; // broom

            return activityType switch
            {
                "task_claimed" => "\ud83d\udccb",
                "task_completed" or "task_done" => "\u2705",
                "task_created" => "\u2795",
                "task_paused" => "\u23f8\ufe0f",
                "task_activated" => "\u25b6\ufe0f",
                "task_status" or "checklist_updated" => "\ud83d\udcdd",
                "helper_added" => "\ud83e\udd1d",
                "helper_removed" => "\ud83d\udc4b",
                "message_sent" => "\ud83d\udcac",
                "session_start" => "\ud83d\ude80",
                "session_end" => "\ud83d\uded1",
                "build" or "BUILD_STARTED" => "\ud83d\udd28",
                "BUILD_SUCCEEDED" => "\u2705",
                "BUILD_FAILED" => "\u274c",
                "PLAN_CREATED" or "PLAN_ACTIVATED" => "\ud83d\udcdd",
                "PLAN_SUBMITTED" => "\ud83d\udce8",
                "PLAN_APPROVED" => "\u2705",
                "PLAN_REJECTED" => "\u274c",
                "error" or "TOOL_FAILED" => "\u274c",
                "warning" => "\u26a0\ufe0f",
                _ => "\u26a1"
            };
        }

        // -------------------------------------------------------------------------
        // Event handlers
        // -------------------------------------------------------------------------

        private void OnTasksUpdated(object sender, List<KanbanTask> tasks)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTasksUpdated(sender, tasks)));
                return;
            }
            RefreshTaskStats();
        }

        private void OnProjectsUpdated(object sender, List<Project> projects)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnProjectsUpdated(sender, projects)));
                return;
            }
            RefreshProjectInfo();
        }

        private void OnBrokerActivityRecorded(object sender, MCPServer.Models.ActivityEvent activity)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnBrokerActivityRecorded(sender, activity)));
                return;
            }
            // Restart the debounce timer — batches rapid-fire events and
            // ensures the ActivityFeedService DB write has completed.
            _activityDebounce?.Stop();
            _activityDebounce?.Start();
        }

        // -------------------------------------------------------------------------
        // Messaging
        // -------------------------------------------------------------------------

        private void SendMessage(object data)
        {
            string json = JsonSerializer.Serialize(data);
            if (_isInitialized)
            {
                PostRawJson(json);
            }
            else
            {
                _pendingMessages.Enqueue(json);
            }
        }

        private void PostJsonMessage(object data)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try
            {
                string json = JsonSerializer.Serialize(data);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        private void PostRawJson(string json)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
        }

        // -------------------------------------------------------------------------
        // Dispose
        // -------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _activityDebounce?.Stop();
                _activityDebounce?.Dispose();
                _activityDebounce = null;

                if (_broker != null)
                {
                    _broker.TasksUpdated -= OnTasksUpdated;
                    _broker.ProjectsUpdated -= OnProjectsUpdated;
                    _broker.ActivityRecorded -= OnBrokerActivityRecorded;
                }

                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                        _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                    _webView.Dispose();
                    _webView = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
