using System;
using System.Collections.Generic;
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
    /// WebView2-based HUD panel with two tabs (task f17777d2): "Active" shows the
    /// terminal's active task checklist (or a slim no-task state), and "Not Active"
    /// lists the project's other open tasks (in_progress then todo, done excluded)
    /// with per-status checklist meters and an Activate action (claim/re-assign +
    /// set-active via the broker). Task lookups are project-scoped via SetProject;
    /// a null/empty project id preserves legacy unscoped behavior. Always visible
    /// as a tab in HudTabContainer.
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
        private string _projectId;
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
                else if (messageType == "set_task_active")
                {
                    HandleSetTaskActive(root);
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
            }
        }

        /// <summary>
        /// Activates a task from the HUD's Not Active tab ({type:"set_task_active", taskId}).
        /// Flow (task f17777d2, Owner-specified):
        ///  - assigned to this terminal's agent → SetTaskActive (auto-parks the current active task)
        ///  - unassigned → ClaimTask then SetTaskActive
        ///  - assigned to ANOTHER agent → native TaskDialog confirm ("assigned to Bob — re-assign
        ///    to Alice?" [Re-assign]/[Cancel]), then ClaimTask(allowReassign:true) + SetTaskActive.
        /// Native dialog by design: WebView2 script dialogs (confirm/alert) are blocking and
        /// unreliable, and C# is the layer that knows both agent names anyway. Runs on the UI
        /// thread (OnWebMessageReceived marshals via InvokeRequired). On success the broker
        /// fires TasksUpdated → OnTasksUpdated → RefreshTask, so the HUD re-renders itself.
        /// </summary>
        private void HandleSetTaskActive(JsonElement root)
        {
            if (!root.TryGetProperty("taskId", out var idEl)) return;
            string taskId = idEl.GetString();
            if (string.IsNullOrWhiteSpace(taskId)) return;
            if (_broker == null || string.IsNullOrEmpty(_terminalName)) return;

            try
            {
                var task = _broker.GetTask(taskId);
                if (task == null)
                {
                    _debugLogService?.Trace("TaskHud", $"SetTaskActive: taskId='{taskId}' not found");
                    NotifyActivateCancelled(taskId);
                    return;
                }

                // Scope guard: never activate a task outside this terminal's project.
                // The Not Active list is already scoped, so this only triggers on a
                // stale/forged message — refuse quietly. (OrdinalIgnoreCase per the
                // ClaimTask cf32b08f family precedent.)
                if (!string.IsNullOrEmpty(_projectId) &&
                    !string.Equals(task.ProjectId, _projectId, StringComparison.OrdinalIgnoreCase))
                {
                    _debugLogService?.Warning("TaskHud", $"SetTaskActive REFUSED: task '{task.Title}' belongs to project '{task.ProjectId}', terminal scoped to '{_projectId}'");
                    NotifyActivateCancelled(taskId);
                    return;
                }

                if (task.Status == "done")
                {
                    NotifyActivateCancelled(taskId);
                    return;
                }

                string assignee = task.Assignee?.Trim();
                bool isMine = string.Equals(assignee, _terminalName, StringComparison.OrdinalIgnoreCase);
                bool isUnassigned = string.IsNullOrEmpty(assignee);

                if (!isMine && !isUnassigned)
                {
                    // Assigned to another agent — confirm the re-assign with the Owner.
                    var reassignButton = new TaskDialogButton("Re-assign");
                    var cancelButton = TaskDialogButton.Cancel;
                    var page = new TaskDialogPage
                    {
                        Caption = "Re-assign task",
                        Heading = $"This task is assigned to {assignee}.",
                        Text = $"Do you want to re-assign it to the currently open terminal {_terminalName}?",
                        Icon = TaskDialogIcon.Warning,
                        DefaultButton = cancelButton
                    };
                    page.Buttons.Add(reassignButton);
                    page.Buttons.Add(cancelButton);

                    if (TaskDialog.ShowDialog(this, page) != reassignButton)
                    {
                        NotifyActivateCancelled(taskId);
                        return; // Owner cancelled — no-op
                    }
                }

                // Claim when the task isn't ours OR isn't in_progress yet. ClaimTask is
                // the only todo→in_progress promoter (TaskService.QueueTaskForTerminal/
                // MakeTaskActive), and SetTaskActive hard-rejects non-in_progress tasks —
                // so a SELF-ASSIGNED todo task must still route through ClaimTask (a
                // same-assignee re-claim is explicitly permitted). Pipeline Run-1
                // debugger HIGH: the previous !isMine-only guard broke exactly the
                // "activate my pre-assigned ticket" case the Not Active tab exists for.
                if (!isMine || !string.Equals(task.Status, "in_progress", StringComparison.Ordinal))
                {
                    var claim = _broker.ClaimTask(taskId, _terminalName, allowReassign: !isUnassigned, expectedProjectId: _projectId);
                    if (claim == null || !claim.Success)
                    {
                        _debugLogService?.Warning("TaskHud", $"SetTaskActive: claim failed for '{task.Title}': {claim?.Error ?? "null result"}");
                        MessageBox.Show(this, $"Could not claim task:\n{claim?.Error ?? "unknown error"}", "Activate task", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        NotifyActivateCancelled(taskId);
                        return;
                    }
                }

                var result = _broker.SetTaskActive(taskId, _terminalName);
                if (result == null || !result.Success)
                {
                    _debugLogService?.Warning("TaskHud", $"SetTaskActive failed for '{task.Title}': {result?.Error ?? "null result"}");
                    MessageBox.Show(this, $"Could not activate task:\n{result?.Error ?? "unknown error"}", "Activate task", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    NotifyActivateCancelled(taskId);
                    return;
                }

                _debugLogService?.Info("TaskHud", $"SetTaskActive: '{task.Title}' activated by '{_terminalName}' (parked: {string.Join(", ", result.PausedTaskTitles ?? new List<string>())})");
            }
            catch (Exception ex)
            {
                _debugLogService?.Error("TaskHud", $"SetTaskActive failed for taskId='{taskId}': {ex.GetType().Name}: {ex.Message}");
                NotifyActivateCancelled(taskId);
            }
        }

        /// <summary>
        /// Tells the HUD JS that an activate request ended WITHOUT the task becoming
        /// active (refused, failed, or Owner-cancelled), so it can drop its
        /// pendingActivateId. An explicit ack is used instead of having the JS clear
        /// the id on the next non-matching hud_data: ClaimTask broadcasts TasksUpdated
        /// BEFORE SetTaskActive completes, so an intermediate refresh with the OLD
        /// active task would race a passive clear and suppress the Active-tab flip
        /// (pipeline Run-1 debugger LOW / reviewer NIT, hardened).
        /// </summary>
        private void NotifyActivateCancelled(string taskId)
        {
            PostJsonMessage(new { type = "activate_cancelled", taskId });
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
                _debugLogService?.Trace("TaskHud", $"SetTerminalName: Broker not ready yet, queuing terminal name '{terminalName}'");
                return;
            }

            _terminalName = terminalName;
            _debugLogService?.Trace("TaskHud", $"SetTerminalName: Updated terminal name to '{terminalName}', refreshing");
            RefreshTask();
        }

        /// <summary>
        /// Sets the project scope for task lookup. When non-empty, the HUD only shows
        /// tasks whose ProjectId matches (strict filter, same semantics as
        /// MessageBroker.GetTasks(projectId)); null/empty preserves the legacy
        /// unscoped behavior for terminals without a project. Safe to call before
        /// Initialize — RefreshTask reads the field at lookup time.
        /// Called by TerminalDocument alongside the sibling HUD panels' SetProject
        /// wiring (StartTerminal + StatusLinePoll re-resolve).
        /// </summary>
        public void SetProject(string projectId)
        {
            if (string.Equals(_projectId, projectId, StringComparison.Ordinal)) return;

            _projectId = projectId;
            _debugLogService?.Trace("TaskHud", $"SetProject: projectId='{projectId ?? "null"}', refreshing");

            if (_broker != null && !string.IsNullOrEmpty(_terminalName))
            {
                RefreshTask();
            }
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
        /// Refreshes the HUD from broker state: resolves the active task for this
        /// terminal AND the project's other open tasks, then sends both in one
        /// hud_data payload so the JS can render the Active / Not Active tabs
        /// from a single message (task f17777d2).
        /// </summary>
        private void RefreshTask()
        {
            if (_broker == null || string.IsNullOrEmpty(_terminalName))
            {
                _debugLogService?.Trace("TaskHud", $"RefreshTask: SKIP broker={(_broker != null)} terminalName='{_terminalName}'");
                return;
            }

            // Project-scoped: null/empty _projectId falls through to all tasks (legacy unscoped).
            var allTasks = _broker.GetTasks(_projectId) ?? new List<KanbanTask>();

            KanbanTask task = FindActiveTask(allTasks);
            if (task != null && IsTaskTerminal(task))
            {
                task = null;
            }

            _debugLogService?.Info("TaskHud", $"RefreshTask: active='{task?.Title ?? "(none)"}' projectId='{_projectId ?? "null"}' totalScopedTasks={allTasks.Count} for terminal '{_terminalName}'");
            SendHudData(task, allTasks);
        }

        /// <summary>
        /// Finds the active task for this terminal within the given (already
        /// project-scoped) task list.
        /// 1. ActivityService lookup (terminal-to-task mapping)
        /// 2. Direct SubStatus match (active task assigned to this terminal)
        /// 3. Any in_progress task assigned to this terminal
        /// All tiers operate on the project-scoped task list, so an agent's active
        /// task in ANOTHER project never surfaces in this terminal's HUD (task f17777d2,
        /// follow-up to 0cd2c868). Null/empty _projectId = legacy unscoped.
        /// </summary>
        private KanbanTask FindActiveTask(List<KanbanTask> allTasks)
        {
            int totalTasks = allTasks?.Count ?? 0;

            _debugLogService?.Trace("TaskHud", $"FindActiveTask: terminal='{_terminalName}' projectId='{_projectId ?? "null"}' totalTasks={totalTasks}");

            if (allTasks == null || allTasks.Count == 0)
            {
                _debugLogService?.Trace("TaskHud", $"FindActiveTask: No tasks available");
                return null;
            }

            // Primary: use ActivityService to get the active task ID
            var activity = _broker.ActivityService?.GetActivity(_terminalName);
            if (activity != null && !string.IsNullOrEmpty(activity.TaskId))
            {
                var task = allTasks.FirstOrDefault(t => t.Id == activity.TaskId);
                if (task != null && task.Status == "in_progress")
                {
                    _debugLogService?.Trace("TaskHud", $"FindActiveTask: PRIMARY match: task='{task.Title}' via ActivityService");
                    return task;
                }
                _debugLogService?.Trace("TaskHud", $"FindActiveTask: PRIMARY miss: activity taskId='{activity.TaskId}' not found or not in_progress");
            }
            else
            {
                _debugLogService?.Trace("TaskHud", $"FindActiveTask: PRIMARY miss: no activity for terminal '{_terminalName}'");
            }

            // Secondary: find the explicitly active task assigned to this terminal
            var activeTask = allTasks.FirstOrDefault(t =>
                string.Equals(t.Assignee, _terminalName, StringComparison.OrdinalIgnoreCase) &&
                t.Status == "in_progress" &&
                t.SubStatus == "active");
            if (activeTask != null)
            {
                _debugLogService?.Trace("TaskHud", $"FindActiveTask: SECONDARY match: task='{activeTask.Title}' via SubStatus=active");
                return activeTask;
            }
            _debugLogService?.Trace("TaskHud", $"FindActiveTask: SECONDARY miss: no in_progress+active task for '{_terminalName}'");

            // Tertiary: any in_progress task assigned to this terminal
            var tertiaryTask = allTasks.FirstOrDefault(t =>
                string.Equals(t.Assignee, _terminalName, StringComparison.OrdinalIgnoreCase) &&
                t.Status == "in_progress");
            if (tertiaryTask != null)
            {
                _debugLogService?.Trace("TaskHud", $"FindActiveTask: TERTIARY match: task='{tertiaryTask.Title}'");
            }
            else
            {
                // Log all in_progress tasks to help diagnose assignee mismatches
                var inProgressTasks = allTasks.Where(t => t.Status == "in_progress").ToList();
                _debugLogService?.Trace("TaskHud", $"FindActiveTask: TERTIARY miss: no match. In-progress tasks ({inProgressTasks.Count}): {string.Join(", ", inProgressTasks.Select(t => $"'{t.Title}' assignee='{t.Assignee}'"))}");
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
        /// Sends the unified HUD payload: the active task (or null) plus the
        /// project's other OPEN tasks for the Not Active tab. Done tasks are
        /// excluded (Owner decision, task f17777d2 — done work lives on the
        /// kanban/lifecycle boards; the HUD is an open-work view). Groups are
        /// ordered in_progress (paused, most actionable) before todo, then by
        /// priority within each group.
        /// </summary>
        private void SendHudData(KanbanTask activeTask, List<KanbanTask> allTasks)
        {
            object activePayload = null;
            if (activeTask != null)
            {
                var checklist = activeTask.GetChecklist() ?? new List<ChecklistItem>();
                var checklistData = checklist.Select(item => new
                {
                    item = item.Item,
                    status = item.Status ?? (item.Done ? "done" : "pending"),
                    assignedTo = item.AssignedTo,
                    cycleCount = item.CycleCount
                }).ToArray();

                activePayload = new
                {
                    id = activeTask.Id,
                    title = activeTask.Title,
                    description = activeTask.Description,
                    priority = activeTask.Priority,
                    status = activeTask.Status,
                    assignee = activeTask.Assignee,
                    checklist = checklistData
                };
            }

            var notActiveTasks = allTasks
                .Where(t => (t.Status == "in_progress" || t.Status == "todo") &&
                            (activeTask == null || !string.Equals(t.Id, activeTask.Id, StringComparison.Ordinal)))
                // JS re-groups by status (renderNotActive filters per group), so only
                // the ThenBy priority order is load-bearing; the status OrderBy just
                // keeps the wire format readable.
                .OrderBy(t => t.Status == "in_progress" ? 0 : 1)
                .ThenBy(t => PriorityRank(t.Priority))
                .Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    description = t.Description,
                    priority = t.Priority,
                    status = t.Status,
                    assignee = t.Assignee,
                    // createdAt drives the client-side Not-Active sort toggle AND the
                    // "Created …" relative-time line (task 08dc9c1f). The DB reads these
                    // back as Kind=Unspecified (the connection sets no DateTimeKind), so
                    // SpecifyKind(...,Utc) is REQUIRED before serialization — otherwise
                    // System.Text.Json emits no 'Z' and JS Date.parse treats the value as
                    // LOCAL, shifting every relative time by the machine's UTC offset
                    // (pipeline RUN 2: debugger FAIL / adversary HIGH). Do NOT use
                    // ToUniversalTime() (would double-shift an Unspecified value) and do NOT
                    // set DateTimeKind on the shared connection (breaks the compensating
                    // .ToUniversalTime() reads at TaskDatabase.cs:3020/3113).
                    createdAt = DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc),
                    // updatedAt is the "last worked on" proxy (tasks.updated_at, bumped on
                    // every save); same UTC-tagging contract as createdAt above.
                    updatedAt = DateTime.SpecifyKind(t.UpdatedAt, DateTimeKind.Utc),
                    counts = CountChecklist(t)
                })
                .ToArray();

            var payload = new
            {
                type = "hud_data",
                terminalName = _terminalName,
                activeTask = activePayload,
                notActiveTasks
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

        private static int PriorityRank(string priority)
        {
            switch ((priority ?? "normal").ToLowerInvariant())
            {
                case "urgent": return 0;
                case "high": return 1;
                case "normal": return 2;
                default: return 3; // low / unknown
            }
        }

        /// <summary>
        /// Per-status checklist item counts for a task's Not Active row mini-meter.
        /// </summary>
        private static object CountChecklist(KanbanTask task)
        {
            int pending = 0, coding = 0, testing = 0, done = 0;
            var checklist = task.GetChecklist();
            if (checklist != null)
            {
                foreach (var item in checklist)
                {
                    switch ((item.Status ?? (item.Done ? "done" : "pending")).ToLowerInvariant())
                    {
                        case "coding": coding++; break;
                        case "testing": testing++; break;
                        case "done": done++; break;
                        default: pending++; break;
                    }
                }
            }

            return new { pending, coding, testing, done, total = pending + coding + testing + done };
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
