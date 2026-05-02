using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.TaskLifecycleBoard;
using MultiTerminal.Terminal;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// WebView2-based HUD panel that shows the active task checklist for a terminal.
    /// Always visible as a tab in HudTabContainer. Shows an empty state with
    /// available tasks when no active task is assigned.
    /// </summary>
    public class TaskHudRenderer : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        // Pending update queued before WebView2 is ready
        private string _pendingMessageJson;

        private MessageBroker _broker;
        private string _terminalName;
        private DebugLogService _debugLogService;

        // Stores the terminal name if SetTerminalName is called before Initialize
        private string _pendingTerminalName;

        /// <summary>
        /// Gets whether Initialize has been called with a broker.
        /// </summary>
        public bool IsBrokerInitialized => _broker != null;

        /// <summary>
        /// Raised when the HUD wants to show or hide itself.
        /// The bool argument is true to show, false to hide.
        /// TerminalDocument subscribes to this to uncollapse/collapse Panel2
        /// BEFORE setting Visible, avoiding the WinForms VisibleChanged deadlock
        /// when Panel2 is collapsed (collapsed Panel2 → parent invisible →
        /// child VisibleChanged never fires → Panel2 never uncollapses).
        /// </summary>
        public event EventHandler<bool> HudVisibilityRequested;

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
        /// Sets the debug log service for diagnostic logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService service) => _debugLogService = service;

        /// <summary>
        /// Gets whether SetTerminalName() was called before Initialize(), leaving a queued name.
        /// Used by TerminalDocument to detect when broker arrives after name was pre-set.
        /// </summary>
        public bool HasPendingTerminalName => !string.IsNullOrEmpty(_pendingTerminalName);

        public TaskHudRenderer()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            Name = "TaskHudRenderer";
            Visible = false; // HudTabContainer manages visibility via SwitchToTab

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                Name = "webView"
            };

            Controls.Add(_webView);
            ResumeLayout(false);

            // Lazy init: only start WebView2 when first made visible
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

                // Set default background to match dark theme to prevent white flash
                _webView.DefaultBackgroundColor = _isDarkTheme
                    ? System.Drawing.Color.FromArgb(26, 26, 46)   // #1a1a2e (dark bg-primary)
                    : System.Drawing.Color.FromArgb(245, 245, 245); // #f5f5f5 (light bg-primary)

                var settings = _webView.CoreWebView2.Settings;
                settings.IsScriptEnabled = true;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.IsStatusBarEnabled = false;

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                string htmlPath = GetHudHtmlPath();
                if (File.Exists(htmlPath))
                {
                    _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    ShowError("Task HUD HTML not found: " + htmlPath);
                    _isInitializing = false;
                }
            }
            catch (Exception ex)
            {
                ShowError("Failed to initialize WebView2: " + ex.Message);
                _isInitializing = false;
            }
        }

        private string GetHudHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Try Controls/TaskHudPanel/task-hud.html
            string path = Path.Combine(assemblyDir, "Controls", "TaskHudPanel", "task-hud.html");
            if (File.Exists(path)) return path;

            // Try TaskHudPanel/task-hud.html
            path = Path.Combine(assemblyDir, "TaskHudPanel", "task-hud.html");
            if (File.Exists(path)) return path;

            // Fallback
            return Path.Combine(assemblyDir, "Controls", "TaskHudPanel", "task-hud.html");
        }

        private void ShowError(string message)
        {
            var errorLabel = new Label
            {
                Text = message,
                ForeColor = System.Drawing.Color.Red,
                BackColor = System.Drawing.Color.FromArgb(30, 30, 30),
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new System.Drawing.Font("Segoe UI", 9f)
            };
            Controls.Clear();
            Controls.Add(errorLabel);
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                ShowError("Failed to load task HUD: " + e.WebErrorStatus);
                _isInitializing = false;
            }
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Defensive marshalling — WebView2 raises this on the UI thread today, but
            // matches the InvokeRequired pattern used by OnTasksUpdated/OnActivityUpdated
            // in this file so a future configuration change can't silently break us.
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnWebMessageReceived(sender, e)));
                return;
            }

            try
            {
                string json = e.WebMessageAsJson;
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                var messageType = typeEl.GetString();

                if (messageType == "ready")
                {
                    _isInitialized = true;
                    _isInitializing = false;

                    // Send current theme
                    PostJsonMessage(new { type = "theme", isDark = _isDarkTheme });

                    // Flush pending task data update if any
                    if (!string.IsNullOrEmpty(_pendingMessageJson))
                    {
                        PostRawJson(_pendingMessageJson);
                        _pendingMessageJson = null;
                    }

                    _webView.ZoomFactorChanged += (s, ev) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
                    if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                        _webView.ZoomFactor = _pendingZoom;
                }
                else if (messageType == "open_lifecycle_board")
                {
                    HandleOpenLifecycle(root);
                }
            }
            catch
            {
                // Ignore malformed messages from WebView2
            }
        }

        /// <summary>
        /// Opens the standalone Lifecycle Board window for the given task. Called when the
        /// HUD JS posts {type:"open_lifecycle_board", taskId} after a click on a task title.
        /// Reuses TaskLifecycleBoardForm.OpenForTask which already handles dedup,
        /// theme propagation, and bounds persistence.
        /// </summary>
        private void HandleOpenLifecycle(JsonElement root)
        {
            if (!root.TryGetProperty("taskId", out var idEl)) return;
            string taskId = idEl.GetString();
            if (string.IsNullOrWhiteSpace(taskId)) return;
            if (_broker == null) return;

            try
            {
                // settings: null is intentional — Form falls back to SettingsService.Default,
                // which is the same singleton TasksPanelControl.cs:357 already passes explicitly.
                // Plumbing _settings through Initialize would require touching MainForm and
                // TerminalDocument; deferred as out-of-scope for Path C's two-file edit envelope.
                TaskLifecycleBoardForm.OpenForTask(taskId, _broker, _isDarkTheme);
            }
            catch (Exception ex)
            {
                _debugLogService?.Trace("TaskHud", $"OpenLifecycle failed for taskId='{taskId}': {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.HandleOpenLifecycle] Failed for taskId='{taskId}': {ex.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Wires this HUD to the broker and registers event subscriptions.
        /// Call after the control handle is created (e.g. from TerminalDocument.SetMessageBroker).
        /// If SetTerminalName() was called before Initialize(), its value takes precedence.
        /// </summary>
        public void Initialize(MessageBroker broker, string terminalName)
        {
            _broker = broker;

            // If SetTerminalName was called before Initialize, prefer that name
            // since it reflects a later, more authoritative registration event
            if (!string.IsNullOrEmpty(_pendingTerminalName))
            {
                _terminalName = _pendingTerminalName;
                _pendingTerminalName = null;
            }
            else
            {
                _terminalName = terminalName;
            }

            if (_broker == null) return;

            // Guard against double-subscribe if Initialize is called more than once
            _broker.TasksUpdated -= OnTasksUpdated;
            _broker.TasksUpdated += OnTasksUpdated;

            if (_broker.ActivityService != null)
            {
                _broker.ActivityService.ActivityUpdated -= OnActivityUpdated;
                _broker.ActivityService.ActivityUpdated += OnActivityUpdated;
            }

            _debugLogService?.Trace("TaskHud", $"Initialize: terminal='{_terminalName}' broker ready, refreshing task");
            System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.Initialize] terminal='{_terminalName}' broker ready, refreshing task");

            // Refresh immediately in case a task is already active
            RefreshTask();
        }

        /// <summary>
        /// Updates the terminal name used for task lookup (called when CustomTitle changes).
        /// If the broker is not yet initialized, stores the name for use when Initialize() is called.
        /// </summary>
        public void SetTerminalName(string terminalName)
        {
            if (_broker == null)
            {
                // Broker not yet set — store so Initialize() can pick it up
                _pendingTerminalName = terminalName;
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.SetTerminalName] Broker not ready yet, queuing terminal name '{terminalName}'");
                return;
            }

            _terminalName = terminalName;
            System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.SetTerminalName] Updated terminal name to '{terminalName}', refreshing");
            RefreshTask();
        }

        /// <summary>
        /// Applies the current theme to the WebView2 HUD.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;
            if (_isInitialized)
            {
                PostJsonMessage(new { type = "theme", isDark = _isDarkTheme });
            }
        }

        // -------------------------------------------------------------------------
        // Event handlers
        // -------------------------------------------------------------------------

        private void OnTasksUpdated(object sender, System.Collections.Generic.List<KanbanTask> tasks)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnTasksUpdated(sender, tasks)));
                return;
            }
            RefreshTask();
        }

        private void OnActivityUpdated(object sender, TerminalActivity activity)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnActivityUpdated(sender, activity)));
                return;
            }

            if (activity.Terminal == _terminalName)
            {
                RefreshTask();
            }
        }

        // -------------------------------------------------------------------------
        // Task lookup and display
        // -------------------------------------------------------------------------

        /// <summary>
        /// Finds the active task for this terminal and sends it to the WebView2 HUD.
        /// Always-on: shows task checklist when active, or empty state with available tasks.
        /// </summary>
        private void RefreshTask()
        {
            if (_broker == null || string.IsNullOrEmpty(_terminalName))
            {
                _debugLogService?.Trace("TaskHud", $"RefreshTask: SKIP broker={(_broker != null)} terminalName='{_terminalName}'");
                return;
            }

            KanbanTask task = FindActiveTask();

            if (task == null || IsTaskTerminal(task))
            {
                // No active task — show empty state with available tasks
                _debugLogService?.Trace("TaskHud", $"RefreshTask: NO active task → showing empty state with available tasks");
                SendEmptyState();
                return;
            }

            var checklist = task.GetChecklist();
            if (checklist == null || checklist.Count == 0)
            {
                // Task has no checklist — show task info but with empty checklist
                _debugLogService?.Trace("TaskHud", $"RefreshTask: Task '{task.Title}' has NO checklist → showing task without items");
                SendTaskData(task);
                return;
            }

            // Task found with checklist — show full data
            _debugLogService?.Info("TaskHud", $"RefreshTask: SHOW '{task.Title}' with {checklist.Count} items for terminal '{_terminalName}'");
            SendTaskData(task);
        }

        /// <summary>
        /// Sends an empty state message to the HUD with a list of available todo tasks.
        /// </summary>
        private void SendEmptyState()
        {
            var allTasks = _broker.GetTasks();
            var todoTasks = allTasks?
                .Where(t => t.Status == "todo" && string.IsNullOrEmpty(t.Assignee))
                .Take(5)
                .Select(t => new { id = t.Id, title = t.Title, priority = t.Priority })
                .ToArray() ?? Array.Empty<object>();

            var payload = new
            {
                type = "empty_state",
                terminalName = _terminalName,
                availableTasks = todoTasks
            };

            string json = JsonSerializer.Serialize(payload);

            if (_isInitialized)
            {
                PostRawJson(json);
            }
            else
            {
                _pendingMessageJson = json;
            }
        }

        /// <summary>
        /// Finds the active task for this terminal.
        /// 1. ActivityService lookup (terminal-to-task mapping)
        /// 2. Direct SubStatus match (active task assigned to this terminal)
        /// 3. Any in_progress task assigned to this terminal
        /// </summary>
        private KanbanTask FindActiveTask()
        {
            var allTasks = _broker.GetTasks();
            int totalTasks = allTasks?.Count ?? 0;

            _debugLogService?.Trace("TaskHud", $"FindActiveTask: terminal='{_terminalName}' totalTasks={totalTasks}");
            System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] terminal='{_terminalName}' totalTasks={totalTasks}");

            if (allTasks == null || allTasks.Count == 0)
            {
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] No tasks available");
                return null;
            }

            // Primary: use ActivityService to get the active task ID
            var activity = _broker.ActivityService?.GetActivity(_terminalName);
            if (activity != null && !string.IsNullOrEmpty(activity.TaskId))
            {
                var task = allTasks.FirstOrDefault(t => t.Id == activity.TaskId);
                if (task != null && task.Status == "in_progress")
                {
                    System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] PRIMARY match: task='{task.Title}' via ActivityService");
                    return task;
                }
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] PRIMARY miss: activity taskId='{activity.TaskId}' not found or not in_progress");
            }
            else
            {
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] PRIMARY miss: no activity for terminal '{_terminalName}'");
            }

            // Secondary: find the explicitly active task assigned to this terminal
            var activeTask = allTasks.FirstOrDefault(t =>
                string.Equals(t.Assignee, _terminalName, StringComparison.OrdinalIgnoreCase) &&
                t.Status == "in_progress" &&
                t.SubStatus == "active");
            if (activeTask != null)
            {
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] SECONDARY match: task='{activeTask.Title}' via SubStatus=active");
                return activeTask;
            }
            System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] SECONDARY miss: no in_progress+active task for '{_terminalName}'");

            // Tertiary: any in_progress task assigned to this terminal
            var tertiaryTask = allTasks.FirstOrDefault(t =>
                string.Equals(t.Assignee, _terminalName, StringComparison.OrdinalIgnoreCase) &&
                t.Status == "in_progress");
            if (tertiaryTask != null)
            {
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] TERTIARY match: task='{tertiaryTask.Title}'");
            }
            else
            {
                // Log all in_progress tasks to help diagnose assignee mismatches
                var inProgressTasks = allTasks.Where(t => t.Status == "in_progress").ToList();
                System.Diagnostics.Trace.WriteLine($"[TaskHudRenderer.FindActiveTask] TERTIARY miss: no match. In-progress tasks ({inProgressTasks.Count}): {string.Join(", ", inProgressTasks.Select(t => $"'{t.Title}' assignee='{t.Assignee}'"))}");
            }
            return tertiaryTask;
        }

        /// <summary>
        /// Returns true when the task is in a terminal state (done or removed).
        /// </summary>
        private bool IsTaskTerminal(KanbanTask task)
        {
            return task.Status == "done";
        }

        /// <summary>
        /// Sends the full task data payload to the WebView2 HUD.
        /// </summary>
        private void SendTaskData(KanbanTask task)
        {
            var checklist = task.GetChecklist();
            var checklistData = checklist.Select(item => new
            {
                item = item.Item,
                status = item.Status ?? (item.Done ? "done" : "pending"),
                assignedTo = item.AssignedTo,
                cycleCount = item.CycleCount
            }).ToArray();

            var payload = new
            {
                type = "task_data",
                task = new
                {
                    id = task.Id,
                    title = task.Title,
                    description = task.Description,
                    priority = task.Priority,
                    status = task.Status,
                    assignee = task.Assignee,
                    checklist = checklistData
                }
            };

            string json = JsonSerializer.Serialize(payload);

            if (_isInitialized)
            {
                PostRawJson(json);
            }
            else
            {
                // Queue for when WebView2 reports ready
                _pendingMessageJson = json;
            }
        }

        // -------------------------------------------------------------------------
        // WebView2 messaging helpers
        // -------------------------------------------------------------------------

        private void PostJsonMessage(object data)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try
            {
                string json = JsonSerializer.Serialize(data);
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // Ignore send failures
            }
        }

        private void PostRawJson(string json)
        {
            if (!_isInitialized || _webView?.CoreWebView2 == null) return;
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch
            {
                // Ignore send failures
            }
        }

        // -------------------------------------------------------------------------
        // Dispose
        // -------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_broker != null)
                {
                    _broker.TasksUpdated -= OnTasksUpdated;
                    if (_broker.ActivityService != null)
                    {
                        _broker.ActivityService.ActivityUpdated -= OnActivityUpdated;
                    }
                }

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
