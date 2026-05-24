using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MultiTerminal.ChatPanel;
using MultiTerminal.Dialogs;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using MultiTerminal.Terminal;

namespace MultiTerminal.TasksPanel
{
    /// <summary>
    /// WebView2-based control for displaying the Kanban task board.
    /// </summary>
    public class TasksPanelControl : UserControl
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private bool _isInitializing;
        private bool _initializePending;
        private bool _isShuttingDown;
        private MessageBroker _broker;
        private CodeReviewService _codeReviewService;
        private ActivityService _activityService;
        private SettingsService _settings;
        private DebugLogService _debugLogService;
        private string _selectedProjectId;

        // Side-channel for WebView2 → WinForms drag-and-drop
        // (WebView2 doesn't reliably pass HTML5 dataTransfer to OLE drag-drop)
        private static readonly object _dragLock = new object();
        private static string _pendingDragTaskId;
        private static string _pendingDragTitle;

        /// <summary>
        /// Try to get the current drag data set by the kanban board.
        /// Returns true if a kanban drag is in progress.
        /// </summary>
        public static bool TryGetPendingDragData(out string taskId, out string title)
        {
            lock (_dragLock)
            {
                taskId = _pendingDragTaskId;
                title = _pendingDragTitle;
                return !string.IsNullOrEmpty(taskId);
            }
        }

        /// <summary>Clear pending drag data (called on drag end or drop).</summary>
        public static void ClearPendingDragData()
        {
            lock (_dragLock)
            {
                _pendingDragTaskId = null;
                _pendingDragTitle = null;
            }
        }
        private bool _isDarkTheme = true;
        private double _pendingZoom = 1.0;

        /// <summary>
        /// Raised when the user requests to inject a task into a terminal.
        /// </summary>
        public event EventHandler<InjectMessageEventArgs> InjectRequested;

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
        /// Set the debug log service for internal debug panel logging.
        /// </summary>
        public void SetDebugLogService(DebugLogService debugLogService)
        {
            _debugLogService = debugLogService;
        }

        private void DebugLog(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[TasksPanel] {message}");
            _debugLogService?.Trace("TasksPanel", message);
        }

        private void DebugLogError(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[TasksPanel] ERROR: {message}");
            _debugLogService?.Error("TasksPanel", message);
        }

        public TasksPanelControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            _webView.WebMessageReceived += OnWebMessageReceived;

            Controls.Add(_webView);

            // Wait for handle before initializing WebView2 (like WebViewTerminalRenderer)
            HandleCreated += OnHandleCreated;
        }

        private async void OnHandleCreated(object sender, EventArgs e)
        {
            DebugLog($"OnHandleCreated fired. initPending={_initializePending}, isInitializing={_isInitializing}, isInitialized={_isInitialized}");
            // Only initialize if Initialize() was already called with a broker
            if (!_initializePending || _isInitializing || _isInitialized) return;
            DebugLog("OnHandleCreated: proceeding to InitializeWebView2Async");
            await InitializeWebView2Async();
        }

        /// <summary>
        /// Initialize the tasks panel with a message broker.
        /// </summary>
        public async void Initialize(MessageBroker broker, ActivityService activityService = null, SettingsService settings = null)
        {
            _broker = broker;
            _codeReviewService = broker != null ? new CodeReviewService(broker) : null;
            _activityService = activityService;
            _settings = settings;

            DebugLog($"Initialize called. HandleCreated={IsHandleCreated}, isInitializing={_isInitializing}, isInitialized={_isInitialized}");

            // Subscribe to broker events
            _broker.TasksUpdated += OnTasksUpdated;
            _broker.TaskClaimed += OnTaskClaimed;
            _broker.TerminalRegistered += OnTerminalChanged;
            _broker.TerminalDisconnected += OnTerminalChanged;
            _broker.HelperSessionUpdated += OnHelperSessionUpdated;
            _broker.ProjectsUpdated += OnProjectsUpdated;
            _broker.ReportSaved += OnReportSaved;

            _initializePending = true;

            // Only initialize WebView2 if handle already exists
            // Otherwise, OnHandleCreated will trigger initialization
            if (IsHandleCreated && !_isInitializing && !_isInitialized)
            {
                DebugLog("Handle exists, initializing WebView2 now");
                await InitializeWebView2Async();
            }
            else
            {
                DebugLog("Waiting for HandleCreated event to initialize WebView2");
            }
        }

        private async System.Threading.Tasks.Task InitializeWebView2Async()
        {
            if (_isInitializing || _isInitialized || _isShuttingDown) return;
            _isInitializing = true;

            DebugLog("InitializeWebView2Async: starting");
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                DebugLog("InitializeWebView2Async: got environment, calling EnsureCoreWebView2Async");
                await _webView.EnsureCoreWebView2Async(env);
                DebugLog("InitializeWebView2Async: EnsureCoreWebView2Async completed");
            }
            catch (OperationCanceledException)
            {
                DebugLog("WebView2 init cancelled (shutdown).");
                _isInitializing = false;
            }
            catch (Exception ex) when (_isShuttingDown || IsDisposed || Disposing)
            {
                DebugLog($"WebView2 init failed during shutdown: {ex.Message}");
                _isInitializing = false;
            }
            catch (Exception ex)
            {
                DebugLogError($"WebView2 init failed: {ex.Message}");
                _isInitializing = false;
            }
        }

        private void OnTerminalChanged(object sender, TerminalInfo e)
        {
            // Update terminal list in WebView when terminals change
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnTerminalChanged(sender, e)));
                return;
            }
            SendTerminalsToWebView();
            SendMembersToWebView();
        }

        private void OnTaskClaimed(object sender, TaskClaimedEventArgs e)
        {
            if (!_isInitialized)
                return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnTaskClaimed(sender, e)));
                return;
            }

            // Send toast notification to WebView
            var message = $"{{\"type\":\"task_claimed\",\"claimedBy\":\"{EscapeJson(e.ClaimedBy)}\",\"taskTitle\":\"{EscapeJson(e.TaskTitle)}\"}}";
            PostMessage(message);
        }

        private string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        /// <summary>
        /// Centralized {"type":"error","message":...} payload emit. Multiple
        /// caller-side gaps (create_task failure, edit_task validation gates,
        /// edit_task late race-coverage) post the same shape with the same
        /// EscapeJson dialect; channeling through one helper keeps the wire
        /// format consistent and avoids 7+ near-identical inline shapes.
        /// </summary>
        private void PostErrorMessage(string message)
        {
            var escaped = EscapeJson(message ?? "Operation failed");
            PostMessage($"{{\"type\":\"error\",\"message\":\"{escaped}\"}}");
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            DebugLog($"OnWebViewInitialized: success={e.IsSuccess}");
            if (!e.IsSuccess)
            {
                DebugLogError($"WebView2 init failed: {e.InitializationException?.Message}");
                if (_isShuttingDown || IsDisposed || Disposing || !IsHandleCreated)
                    return;
                MessageBox.Show($"WebView2 initialization failed: {e.InitializationException?.Message}",
                    "Tasks Panel Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load the HTML using robust path searching
            var htmlPath = GetHtmlPath();
            DebugLog($"HTML path resolved: {htmlPath}, exists={File.Exists(htmlPath)}");
            if (File.Exists(htmlPath))
            {
                var uri = new Uri(htmlPath).AbsoluteUri;
                DebugLog($"Navigating to: {uri}");
                _webView.CoreWebView2.Navigate(uri);
            }
            else
            {
                DebugLogError($"HTML NOT FOUND at: {htmlPath}");
                _webView.CoreWebView2.NavigateToString($"<html><body><h1>Tasks panel HTML not found</h1><p>Searched: {htmlPath}</p></body></html>");
            }

            _isInitialized = true;
            DebugLog("_isInitialized set to true");

            _webView.ZoomFactorChanged += (s, e) => ZoomChanged?.Invoke(this, _webView.ZoomFactor);
            if (Math.Abs(_pendingZoom - 1.0) > 0.01)
                _webView.ZoomFactor = _pendingZoom;
        }

        private string GetHtmlPath()
        {
            // Try to find tasks-panel.html relative to the assembly location
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Check in TasksPanel subfolder
            string path = Path.Combine(assemblyDir, "TasksPanel", "tasks-panel.html");
            if (File.Exists(path)) return path;

            // Check in same folder as assembly
            path = Path.Combine(assemblyDir, "tasks-panel.html");
            if (File.Exists(path)) return path;

            // Check in parent folder's TasksPanel subfolder (for development)
            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "TasksPanel", "tasks-panel.html");
                if (File.Exists(path)) return path;
            }

            // Try AppDomain base directory as last resort
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TasksPanel", "tasks-panel.html");
            if (File.Exists(path)) return path;

            return Path.Combine(assemblyDir, "TasksPanel", "tasks-panel.html");
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var messageType = typeElement.GetString();
                DebugLog($"WebMessage received: type={messageType}");

                switch (messageType)
                {
                    case "ready":
                        OnPanelReady();
                        break;

                    case "task_drag_start":
                        {
                            var dragTaskId = root.TryGetProperty("taskId", out var dtid) ? dtid.GetString() : null;
                            var dragTitle = root.TryGetProperty("title", out var dtt) ? dtt.GetString() : "";
                            if (!string.IsNullOrEmpty(dragTaskId))
                            {
                                lock (_dragLock)
                                {
                                    _pendingDragTaskId = dragTaskId;
                                    _pendingDragTitle = dragTitle;
                                }
                                DebugLog($"Drag started: task={dragTaskId}");

                                // Bridge WebView2 HTML5 drag → WinForms OLE drag.
                                // HTML5 drag events don't cross the WebView2 boundary, so we
                                // start a proper OLE DoDragDrop while the mouse button is still held.
                                var payload = JsonSerializer.Serialize(new { taskId = dragTaskId, title = dragTitle, source = "multiterminal-kanban" });
                                var dataObj = new DataObject(DataFormats.Text, payload);
                                BeginInvoke(new Action(() =>
                                {
                                    _webView.DoDragDrop(dataObj, DragDropEffects.Move | DragDropEffects.Copy);
                                    // OLE drag ended (user released mouse) — clear side-channel
                                    ClearPendingDragData();
                                }));
                            }
                        }
                        break;

                    case "task_drag_end":
                        ClearPendingDragData();
                        DebugLog("Drag ended");
                        break;

                    case "open_lifecycle_board":
                        if (root.TryGetProperty("taskId", out var lifecycleTaskIdEl))
                        {
                            var taskId = lifecycleTaskIdEl.GetString();
                            TaskLifecycleBoard.TaskLifecycleBoardForm.OpenForTask(taskId, _broker, _isDarkTheme, _settings);
                        }
                        break;

                    case "code_review_verdict":
                        if (root.TryGetProperty("taskId", out var crTaskIdEl) &&
                            root.TryGetProperty("verdict", out var crVerdictEl))
                        {
                            var crTaskId = crTaskIdEl.GetString();
                            var crVerdict = crVerdictEl.GetString();
                            // Check for inline review notes (submit notes flow)
                            string reviewNotesJson = null;
                            if (root.TryGetProperty("reviewNotes", out var reviewNotesEl))
                            {
                                reviewNotesJson = reviewNotesEl.GetRawText();
                            }
                            HandleCodeReviewVerdict(crTaskId, crVerdict, reviewNotesJson);
                        }
                        break;

                    case "open_code_review_popup":
                        // Kanban-card Code Review icon → open the standalone popup
                        // via the manager. Single-instance per taskId; focuses if
                        // already open. Independent of Tasks panel state.
                        if (root.TryGetProperty("taskId", out var crpTaskIdEl))
                        {
                            var crpTaskId = crpTaskIdEl.GetString();
                            var crpFilePath = root.TryGetProperty("filePath", out var crpFpEl) ? crpFpEl.GetString() : null;
                            OpenCodeReviewPopup(crpTaskId, crpFilePath);
                        }
                        break;

                    case "create_task":
                        if (root.TryGetProperty("title", out var titleEl) &&
                            root.TryGetProperty("createdBy", out var createdByEl))
                        {
                            var description = root.TryGetProperty("description", out var descEl)
                                ? descEl.GetString() : "";
                            var status = root.TryGetProperty("status", out var createStatusEl)
                                ? createStatusEl.GetString() : "todo";
                            var priority = root.TryGetProperty("priority", out var createPriorityEl)
                                ? createPriorityEl.GetString() : "normal";
                            var projectId = root.TryGetProperty("projectId", out var createProjectIdEl)
                                ? createProjectIdEl.GetString() : null;
                            var checklistJson = root.TryGetProperty("checklistJson", out var createChecklistJsonEl)
                                ? createChecklistJsonEl.GetString() : "[]";

                            var result = _broker?.CreateTask(titleEl.GetString(), description, createdByEl.GetString(), status, priority, projectId);

                            // If creation succeeded and we have checklistJson or helpers, update the task
                            if (result?.Success == true)
                            {
                                var taskId = result.TaskId;
                                if (checklistJson != "[]")
                                {
                                    _broker?.UpdateTaskChecklist(taskId, checklistJson);
                                }

                                // Set assignee if provided
                                if (root.TryGetProperty("assignee", out var assigneeEl))
                                {
                                    var assignee = assigneeEl.GetString();
                                    if (!string.IsNullOrEmpty(assignee))
                                    {
                                        _broker?.UpdateTaskAssignee(taskId, assignee);
                                    }
                                }

                                // Add helpers if provided
                                if (root.TryGetProperty("helpers", out var helpersEl))
                                {
                                    var helpersJson = helpersEl.GetString();
                                    if (!string.IsNullOrEmpty(helpersJson) && helpersJson != "[]")
                                    {
                                        try
                                        {
                                            var helpers = System.Text.Json.JsonSerializer.Deserialize<string[]>(helpersJson);
                                            if (helpers != null && _broker != null)
                                            {
                                                foreach (var helper in helpers)
                                                {
                                                    await _broker.AddHelper(taskId, helper, createdByEl.GetString());
                                                }
                                            }
                                        }
                                        catch
                                        {
                                            // Silently ignore invalid helper JSON
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Surface CreateTask failure (e.g., ambiguous projectId)
                                // and skip the checklist/assignee/helpers follow-ups —
                                // those would target a non-existent task and produce
                                // additional silent failures.
                                PostErrorMessage(result?.Error ?? "Failed to create task");
                            }
                        }
                        break;

                    case "claim_task":
                        if (root.TryGetProperty("taskId", out var claimTaskIdEl))
                        {
                            var assignee = root.TryGetProperty("assignee", out var assigneeEl)
                                ? assigneeEl.GetString() : "User";
                            _broker?.ClaimTask(claimTaskIdEl.GetString(), assignee);
                        }
                        break;

                    case "update_task":
                        if (root.TryGetProperty("taskId", out var updateTaskIdEl) &&
                            root.TryGetProperty("status", out var statusEl))
                        {
                            _broker?.UpdateTaskStatus(updateTaskIdEl.GetString(), statusEl.GetString());
                        }
                        break;

                    case "reorder_task":
                        // Same-column reorder OR cross-column drag with explicit
                        // sort_order. Frontend (handleDrop) computes the midpoint
                        // sort_order from neighbor cards and sends {taskId,
                        // newStatus, newSortOrder, reorderedBy}. Broker applies
                        // both status (if changed) and sort_order in one call so
                        // a single TasksUpdated broadcast covers the move.
                        if (root.TryGetProperty("taskId", out var reorderTaskIdEl) &&
                            root.TryGetProperty("newStatus", out var reorderStatusEl) &&
                            root.TryGetProperty("newSortOrder", out var reorderSortEl))
                        {
                            var reorderTaskId = reorderTaskIdEl.GetString();
                            var reorderStatus = reorderStatusEl.GetString();
                            // Tolerate Number or String wire shapes — handleDrop
                            // sends a JS number but a future agent-driven caller
                            // could stringify it.
                            double newSortOrder;
                            if (reorderSortEl.ValueKind == JsonValueKind.Number)
                            {
                                newSortOrder = reorderSortEl.GetDouble();
                            }
                            else if (reorderSortEl.ValueKind == JsonValueKind.String
                                     && double.TryParse(reorderSortEl.GetString(),
                                         System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         out var parsedSort))
                            {
                                newSortOrder = parsedSort;
                            }
                            else
                            {
                                PostErrorMessage($"Cannot reorder task: 'newSortOrder' must be a number (got {reorderSortEl.ValueKind}).");
                                break;
                            }

                            // NaN/Infinity guard. Covers both branches above:
                            // (a) JSON Number overflow (e.g. 1e500) deserializes
                            // to ±Infinity via GetDouble(); (b) String parse with
                            // NumberStyles.Float accepts the literals "NaN",
                            // "Infinity", "-Infinity". Non-finite values poison
                            // the broker's neighbor comparison loop (NaN < x and
                            // NaN > x are both false) so the rebalance guard
                            // wouldn't fire and SortOrder = NaN would persist.
                            // Defence-in-depth alongside the broker + REST
                            // endpoint guards. Pipeline Codex security MED.
                            if (!double.IsFinite(newSortOrder))
                            {
                                PostErrorMessage("Cannot reorder task: 'newSortOrder' must be a finite number (NaN/Infinity rejected).");
                                break;
                            }

                            var reorderedBy = root.TryGetProperty("reorderedBy", out var reorderedByEl)
                                ? reorderedByEl.GetString() : null;

                            var reorderResult = _broker?.ReorderTask(reorderTaskId, reorderStatus, newSortOrder, reorderedBy);
                            if (reorderResult?.Success == false)
                            {
                                PostErrorMessage(reorderResult.Error ?? "Failed to reorder task");
                            }
                        }
                        break;

                    case "update_checklist":
                        if (root.TryGetProperty("taskId", out var checklistTaskIdEl) &&
                            root.TryGetProperty("checklistJson", out var checklistJsonEl))
                        {
                            _broker?.UpdateTaskChecklist(checklistTaskIdEl.GetString(), checklistJsonEl.GetString());
                        }
                        break;

                    case "get_tasks":
                        SendTasksToWebView();
                        break;

                    case "complete_task":
                        if (root.TryGetProperty("taskId", out var completeTaskIdEl))
                        {
                            _broker?.UpdateTaskStatus(completeTaskIdEl.GetString(), "done");
                        }
                        break;

                    case "edit_task":
                        if (root.TryGetProperty("taskId", out var editTaskIdEl) &&
                            root.TryGetProperty("title", out var editTitleEl))
                        {
                            var taskId = editTaskIdEl.GetString();
                            var editDescription = root.TryGetProperty("description", out var editDescEl)
                                ? editDescEl.GetString() : "";
                            var editedBy = root.TryGetProperty("editedBy", out var editedByEl)
                                ? editedByEl.GetString() : null;

                            // Extract plan, implementation, test results if provided
                            var plan = root.TryGetProperty("plan", out var planEl)
                                ? planEl.GetString() : null;
                            var implementation = root.TryGetProperty("implementation", out var implEl)
                                ? implEl.GetString() : null;
                            var testResults = root.TryGetProperty("testResults", out var testEl)
                                ? testEl.GetString() : null;

                            // Pre-validate ALL fields BEFORE any state mutation.
                            // The edit is treated as one user-level operation —
                            // any invalid input must abort the whole edit so the
                            // user doesn't see (e.g.) the title rename land
                            // while a bad priority is silently rejected. The
                            // existing per-field broker methods each validate
                            // and return Success=false on bad input but only
                            // AFTER prior fields already persisted; we close
                            // that gap here by validating up-front.
                            //
                            // ValueKind guard for projectId: a malformed message
                            // sending projectId as a non-string (Number/Array/
                            // Object) would throw InvalidOperationException out
                            // of GetString() and be silently swallowed by the
                            // outer try/catch. Reject the shape early.
                            bool hasProjectId = root.TryGetProperty("projectId", out var editProjectIdEl);
                            string editProjectIdRaw = null;
                            if (hasProjectId)
                            {
                                if (editProjectIdEl.ValueKind != JsonValueKind.String
                                    && editProjectIdEl.ValueKind != JsonValueKind.Null)
                                {
                                    PostErrorMessage($"Cannot save task: 'projectId' must be a string or null (got {editProjectIdEl.ValueKind}).");
                                    break;
                                }
                                editProjectIdRaw = editProjectIdEl.GetString();
                            }

                            // Title cannot be empty (UpdateTask would reject it
                            // anyway, but only after we'd have no way to know
                            // what else might have already partially applied —
                            // here it's still pre-mutation, so safe to bail).
                            var editTitleStr = editTitleEl.GetString();
                            if (string.IsNullOrWhiteSpace(editTitleStr))
                            {
                                PostErrorMessage("Cannot save task: title cannot be empty.");
                                break;
                            }

                            // Priority must be one of the documented values.
                            string editPriorityStr = null;
                            bool hasPriority = root.TryGetProperty("priority", out var editPriorityEl);
                            if (hasPriority)
                            {
                                editPriorityStr = editPriorityEl.GetString();
                                if (editPriorityStr != "urgent" && editPriorityStr != "normal" && editPriorityStr != "low")
                                {
                                    PostErrorMessage($"Cannot save task: invalid priority '{editPriorityStr}' (expected urgent | normal | low).");
                                    break;
                                }
                            }

                            // Status must be one of the documented values.
                            string editStatusStr = null;
                            bool hasStatus = root.TryGetProperty("status", out var editStatusEl);
                            if (hasStatus)
                            {
                                editStatusStr = editStatusEl.GetString();
                                if (editStatusStr != "todo" && editStatusStr != "in_progress" && editStatusStr != "done" && editStatusStr != "suggestion")
                                {
                                    PostErrorMessage($"Cannot save task: invalid status '{editStatusStr}' (expected todo | in_progress | done | suggestion).");
                                    break;
                                }
                            }

                            // Project ambiguity dry-run. Capture the canonical
                            // id so we pass exactly what we validated to
                            // UpdateTaskProject, rather than re-resolving the
                            // raw string at apply time. Without this thread,
                            // a registry mutation in the microseconds between
                            // dry-run and apply could resolve the same input
                            // differently — pedantic check/use split, but
                            // free to close.
                            string editProjectIdToApply = editProjectIdRaw;
                            if (hasProjectId && _broker != null)
                            {
                                string canonicalProjectId = _broker.TryNormalizeProjectId(editProjectIdRaw, out bool projectAmbiguous);
                                if (projectAmbiguous)
                                {
                                    PostErrorMessage($"Project id '{editProjectIdRaw}' is ambiguous (matches multiple registered projects, or is a short prefix of one); edit not applied. Pass the full id.");
                                    break;
                                }
                                // canonicalProjectId may be null when the input
                                // was empty/whitespace (caller intent: clear
                                // binding) — pass null through to preserve
                                // that semantic. For a registered key or
                                // unique-prefix expansion, pass the canonical.
                                // For an unknown raw, canonical equals trimmed
                                // raw — pass-through is identical to the
                                // pre-fix behaviour.
                                editProjectIdToApply = canonicalProjectId;
                            }

                            // All fields validated. Apply via the existing
                            // per-field broker methods. Cross-SaveTask atomicity
                            // for runtime persistence exceptions is NOT
                            // guaranteed here — the per-field methods each call
                            // SaveTask and a mid-sequence DB failure can still
                            // half-apply. Closing that requires either a
                            // transaction-aware EditTask broker method or
                            // SQLite transactions across the per-field calls;
                            // both are out of scope for this task and tracked
                            // as follow-up. The pre-validation above closes
                            // the dominant failure mode (invalid input).

                            // Update title, description, and documentation fields
                            _broker?.UpdateTask(taskId, editTitleStr, editDescription, editedBy, plan, implementation, testResults);

                            // Update priority if provided
                            if (hasPriority)
                            {
                                _broker?.UpdateTaskPriority(taskId, editPriorityStr);
                            }

                            // Update project if provided
                            if (hasProjectId)
                            {
                                var projResult = _broker?.UpdateTaskProject(taskId, editProjectIdToApply);
                                if (projResult?.Success == false)
                                {
                                    // Race: project registry shifted between
                                    // pre-validate and apply, OR persistence
                                    // failed (Run 4 fix returns Success=false
                                    // and reverts the in-memory mutation in
                                    // that case). Surface the late failure
                                    // so the user knows the project change
                                    // didn't apply.
                                    PostErrorMessage(projResult.Error ?? "Failed to update project");
                                }
                            }

                            // Update status if provided
                            if (hasStatus)
                            {
                                _broker?.UpdateTaskStatus(taskId, editStatusStr);
                            }

                            // Update assignee if provided
                            if (root.TryGetProperty("assignee", out var editAssigneeEl))
                            {
                                _broker?.UpdateTaskAssignee(taskId, editAssigneeEl.GetString());
                            }

                            // Update continuation notes if provided
                            if (root.TryGetProperty("continuationNotes", out var contNotesEl))
                            {
                                var contNotes = contNotesEl.GetString();
                                _broker?.UpdateTaskContinuation(taskId, contNotes, editedBy);
                            }
                        }
                        break;

                    case "delete_task":
                        if (root.TryGetProperty("taskId", out var deleteTaskIdEl))
                        {
                            var deletedBy = root.TryGetProperty("deletedBy", out var deletedByEl)
                                ? deletedByEl.GetString() : null;
                            _broker?.DeleteTask(deleteTaskIdEl.GetString(), deletedBy);
                        }
                        break;

                    case "assign_to_terminal":
                        if (root.TryGetProperty("taskId", out var assignTaskIdEl) &&
                            root.TryGetProperty("terminalName", out var terminalNameEl) &&
                            root.TryGetProperty("taskTitle", out var taskTitleEl))
                        {
                            var taskId = assignTaskIdEl.GetString();
                            var terminalName = terminalNameEl.GetString();
                            var taskTitle = taskTitleEl.GetString();
                            var taskDescription = root.TryGetProperty("taskDescription", out var taskDescEl)
                                ? taskDescEl.GetString() : "";

                            // Update the task assignee (this will trigger TasksUpdated event)
                            _broker?.UpdateTaskAssignee(taskId, terminalName);

                            // Only inject message to terminal if assigning (not unassigning)
                            if (!string.IsNullOrEmpty(terminalName))
                            {
                                // Format the message to inject
                                var content = $"[Task assigned] {taskTitle}";
                                if (!string.IsNullOrEmpty(taskDescription))
                                {
                                    // Add first line of description as context
                                    var firstLine = taskDescription.Split('\n')[0];
                                    if (firstLine.Length > 100) firstLine = firstLine.Substring(0, 100) + "...";
                                    content += $"\n{firstLine}";
                                }

                                // Raise event for MainForm to handle injection
                                InjectRequested?.Invoke(this, new InjectMessageEventArgs
                                {
                                    TerminalName = terminalName,
                                    Content = content
                                });
                            }
                        }
                        break;

                    case "get_projects":
                        SendProjectsToWebView();
                        break;

                    case "select_project":
                        if (root.TryGetProperty("projectId", out var projectIdEl))
                        {
                            var projectId = projectIdEl.GetString();
                            _selectedProjectId = string.IsNullOrEmpty(projectId) ? null : projectId;
                            SendTasksToWebView();
                        }
                        break;

                    case "add_helper":
                        if (root.TryGetProperty("taskId", out var addHelperTaskIdEl) &&
                            root.TryGetProperty("helperName", out var helperNameEl))
                        {
                            var taskId = addHelperTaskIdEl.GetString();
                            var helperName = helperNameEl.GetString();
                            var addedBy = "User"; // Could be enhanced to pass actual user name
                            if (_broker != null) await _broker.AddHelper(taskId, helperName, addedBy);
                            SendTasksToWebView(); // Refresh to show updated helpers
                        }
                        break;

                    case "remove_helper":
                        if (root.TryGetProperty("taskId", out var removeHelperTaskIdEl) &&
                            root.TryGetProperty("helperName", out var removeHelperNameEl))
                        {
                            var taskId = removeHelperTaskIdEl.GetString();
                            var helperName = removeHelperNameEl.GetString();
                            _broker?.RemoveHelper(taskId, helperName);
                            SendTasksToWebView(); // Refresh to show updated helpers
                        }
                        break;

                    case "request_help":
                        if (root.TryGetProperty("taskId", out var requestHelpTaskIdEl) &&
                            root.TryGetProperty("helperName", out var requestHelpHelperNameEl) &&
                            root.TryGetProperty("message", out var helpMessageEl))
                        {
                            var taskId = requestHelpTaskIdEl.GetString();
                            var helperName = requestHelpHelperNameEl.GetString();
                            var message = helpMessageEl.GetString();
                            var fromTerminal = "User"; // Could be enhanced to pass actual terminal name
                            if (_broker != null) await _broker.RequestHelp(taskId, fromTerminal, helperName, message);
                        }
                        break;

                    case "respond_to_stale":
                        if (root.TryGetProperty("taskId", out var respondStaleTaskIdEl) &&
                            root.TryGetProperty("response", out var staleResponseEl))
                        {
                            var taskId = respondStaleTaskIdEl.GetString();
                            var response = staleResponseEl.GetString();
                            var terminalName = "User"; // Could be enhanced to pass actual terminal name
                            _broker?.RespondToStale(taskId, response, terminalName);
                            SendTasksToWebView(); // Refresh to show updated stale status
                        }
                        break;

                    case "spawn_native_helper":
                        if (root.TryGetProperty("prompt", out var helperPromptEl) &&
                            root.TryGetProperty("spawnedBy", out var spawnedByEl))
                        {
                            var prompt = helperPromptEl.GetString();
                            var spawnedBy = spawnedByEl.GetString();
                            var taskId = root.TryGetProperty("taskId", out var helperTaskIdEl)
                                ? helperTaskIdEl.GetString() : null;

                            // Track helper spawn via MessageBroker
                            var result = _broker?.TrackHelperSpawn(taskId, prompt, spawnedBy);
                            if (result != null)
                            {
                                System.Diagnostics.Debug.WriteLine($"Helper spawned: {result.HelperId} (success={result.Success})");
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing tasks panel message: {ex.Message}");
            }
        }

        private void OnPanelReady()
        {
            DebugLog("OnPanelReady: JS sent 'ready' signal");
            // Send pending theme before data so the panel renders in the correct mode
            PostWebMessage($"theme:{(_isDarkTheme ? "dark" : "light")}");
            // Send current tasks, terminals, members, and projects
            SendTasksToWebView();
            SendTerminalsToWebView();
            SendMembersToWebView();
            SendProjectsToWebView();
            DebugLog("OnPanelReady: all data sent to WebView");
        }

        /// <summary>
        /// Send registered terminals to the WebView for the assign dropdown.
        /// </summary>
        private void SendTerminalsToWebView()
        {
            if (!_isInitialized || _broker == null)
                return;

            var terminals = _broker.GetTerminals();

            // Get activity status for each terminal if available
            var terminalData = terminals.Select(t =>
            {
                var status = "unknown";
                if (_activityService != null)
                {
                    var activity = _activityService.GetActivity(t.Name);
                    if (activity != null)
                        status = activity.Status ?? "unknown";
                }
                return new
                {
                    id = t.Id,
                    name = t.Name,
                    color = t.Color,
                    status = status
                };
            });

            var terminalsJson = JsonSerializer.Serialize(terminalData, JsonOptions.Unicode);
            PostMessage($"{{\"type\":\"terminals\",\"terminals\":{terminalsJson}}}");
        }

        /// <summary>
        /// Send team members (from profiles + terminals) to the WebView for the assign dropdown,
        /// grouped by online/offline status.
        /// </summary>
        private void SendMembersToWebView()
        {
            if (!_isInitialized || _broker == null)
                return;

            var terminals = _broker.GetTerminals();
            var terminalNames = terminals?.Select(t => t.Name).ToList() ?? new List<string>();

            // Get all team member profiles (online + offline) for assignee dropdowns
            var profilesResult = _broker.ListProfiles();
            var memberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allMembers = new List<object>();
            if (profilesResult?.Success == true && profilesResult.Profiles != null)
            {
                foreach (var p in profilesResult.Profiles)
                {
                    var displayName = p.DisplayName ?? p.Id;
                    memberNames.Add(displayName);
                    allMembers.Add(new { name = displayName, isOnline = p.IsOnline });
                }
            }
            // Also include any online terminals not yet in profiles
            foreach (var name in terminalNames)
            {
                if (!memberNames.Contains(name))
                    allMembers.Add(new { name, isOnline = true });
            }

            var membersJson = JsonSerializer.Serialize(allMembers, JsonOptions.Unicode);
            PostMessage($"{{\"type\":\"members\",\"members\":{membersJson}}}");
        }

        private void OnTasksUpdated(object sender, List<KanbanTask> tasks)
        {
            DebugLog($"OnTasksUpdated event: {tasks?.Count ?? -1} tasks, isInitialized={_isInitialized}");
            if (!_isInitialized)
            {
                DebugLog("OnTasksUpdated: SKIPPED - not initialized yet");
                return;
            }

            // Marshal to UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnTasksUpdated(sender, tasks)));
                return;
            }

            SendTasksToWebView(tasks);
        }

        private void OnHelperSessionUpdated(object sender, HelperSession session)
        {
            if (!_isInitialized)
                return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnHelperSessionUpdated(sender, session)));
                return;
            }

            // Push helper session update to WebView
            var sessionJson = JsonSerializer.Serialize(new
            {
                id = session.Id,
                taskId = session.TaskId,
                prompt = session.Prompt,
                spawnedBy = session.SpawnedBy,
                spawnedAt = session.SpawnedAt.ToString("o"),
                completedAt = session.CompletedAt?.ToString("o"),
                status = session.Status
            }, JsonOptions.Unicode);

            PostMessage($"{{\"type\":\"helper_session_updated\",\"session\":{sessionJson}}}");
        }

        private void OnProjectsUpdated(object sender, List<Project> projects)
        {
            if (!_isInitialized)
                return;

            // Marshal to UI thread
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnProjectsUpdated(sender, projects)));
                return;
            }

            SendProjectsToWebView();
        }

        private void OnReportSaved(object sender, ReportSavedEventArgs e)
        {
            if (!_isInitialized)
                return;

            if (InvokeRequired)
            {
                Invoke(new Action(() => OnReportSaved(sender, e)));
                return;
            }

            PostMessage($"{{\"type\":\"task_reports_updated\",\"taskId\":\"{EscapeJson(e.TaskId)}\",\"agentName\":\"{EscapeJson(e.AgentName)}\",\"verdict\":\"{EscapeJson(e.Verdict)}\"}}");
        }

        /// <summary>
        /// Send current tasks to the WebView.
        /// </summary>
        private void SendTasksToWebView(List<KanbanTask> tasks = null)
        {
            if (!_isInitialized || _broker == null)
            {
                DebugLog($"SendTasksToWebView SKIPPED: initialized={_isInitialized}, broker={_broker != null}");
                return;
            }

            tasks ??= _broker.GetTasks(_selectedProjectId);
            DebugLog($"SendTasksToWebView: {tasks?.Count ?? -1} tasks from broker, projectFilter={_selectedProjectId ?? "none"}");

            // Apply project filter if set
            if (!string.IsNullOrEmpty(_selectedProjectId) && tasks != null)
            {
                var beforeCount = tasks.Count;
                tasks = tasks.Where(t => t.ProjectId == _selectedProjectId).ToList();
                DebugLog($"SendTasksToWebView: project filter applied, {beforeCount} -> {tasks.Count} tasks");
            }

            try
            {
                // Log a few task statuses for debugging
                if (tasks != null && tasks.Count > 0)
                {
                    var statusCounts = tasks.GroupBy(t => t.Status).Select(g => $"{g.Key}={g.Count()}");
                    DebugLog($"SendTasksToWebView: status breakdown: {string.Join(", ", statusCounts)}");
                }

                var tasksJson = JsonSerializer.Serialize(tasks.Select(t => new
                {
                    id = t.Id,
                    title = t.Title,
                    description = t.Description,
                    status = t.Status,
                    assignee = t.Assignee,
                    createdBy = t.CreatedBy,
                    createdAt = t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    projectId = t.ProjectId,
                    Priority = t.Priority ?? "normal",
                    Helpers = t.Helpers,
                    ChecklistJson = t.ChecklistJson ?? "[]",
                    StaleLevel = t.StaleLevel,
                    DaysPaused = t.PausedAt.HasValue ? (int)(DateTime.UtcNow - t.PausedAt.Value).TotalDays : 0,
                    Plan = t.Plan,
                    ImplementationSummary = t.ImplementationSummary,
                    TestResults = t.TestResults,
                    ContinuationNotes = t.ContinuationNotes,
                    SubStatus = t.SubStatus,
                    ImplementationChecklistJson = t.ImplementationChecklistJson ?? "[]",
                    AutoStatus = t.AutoStatus,
                    sortOrder = t.SortOrder
                }), JsonOptions.Unicode);

                DebugLog($"SendTasksToWebView: JSON serialized, length={tasksJson.Length}");
                var fullMessage = $"{{\"type\":\"task_update\",\"tasks\":{tasksJson}}}";
                DebugLog($"SendTasksToWebView: posting message, total length={fullMessage.Length}");
                PostMessage(fullMessage);
                DebugLog("SendTasksToWebView: PostMessage completed OK");
            }
            catch (Exception ex)
            {
                DebugLogError($"SendTasksToWebView: {ex.Message}\nStack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Refresh the projects dropdown from the broker.
        /// Called externally after ProjectService is wired up.
        /// </summary>
        public void RefreshProjects()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(RefreshProjects));
                return;
            }

            SendProjectsToWebView();
        }

        /// <summary>
        /// Send projects list to the WebView.
        /// </summary>
        private void SendProjectsToWebView()
        {
            if (!_isInitialized || _broker == null)
                return;

            var projects = _broker.GetProjects();

            var projectsJson = JsonSerializer.Serialize(projects.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                description = p.Description,
                taskCount = p.TaskCount
            }), JsonOptions.Unicode);

            PostMessage($"{{\"type\":\"projects\",\"projects\":{projectsJson}}}");
        }

        /// <summary>
        /// Apply a theme to the tasks panel.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            _isDarkTheme = isDark;

            if (!_isInitialized)
                return;

            PostWebMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        /// <summary>
        /// Sets the font size for the tasks panel.
        /// </summary>
        public void SetFontSize(float size)
        {
            if (!_isInitialized)
                return;

            size = Math.Max(8f, Math.Min(14f, size));
            PostWebMessage($"fontSize:{size}");
        }

        /// <summary>
        /// Open (or focus) the standalone Code Review popup for a task. Routes
        /// through <see cref="CodeReviewPopupManager"/> — single-instance per
        /// taskId, independent of the Tasks panel being open. Replaces the
        /// previous in-panel overlay path; the panel kanban-card icon, the
        /// HUD Git right-click escalation, and external callers
        /// (TasksPanelDocument.OpenCodeReview) all share this entry point.
        /// </summary>
        public void OpenCodeReview(string taskId, string filePath = null)
        {
            if (string.IsNullOrEmpty(taskId)) return;

            string taskTitle = string.Empty;
            try
            {
                var task = _broker?.GetTask(taskId);
                if (task != null) taskTitle = task.Title ?? string.Empty;
            }
            catch
            {
                // Lookup failure is non-fatal — popup title falls back to taskId.
            }

            try
            {
                CodeReviewPopupManager.OpenOrFocus(
                    taskId,
                    taskTitle,
                    filePath,
                    _isDarkTheme,
                    _broker,
                    _codeReviewService,
                    FindForm());
            }
            catch (Exception ex)
            {
                DebugLogError($"OpenCodeReview popup failed: {ex.Message}");
            }
        }

        // Internal entry point for the open_code_review_popup web message —
        // identical behavior to the public OpenCodeReview.
        private void OpenCodeReviewPopup(string taskId, string filePath) => OpenCodeReview(taskId, filePath);

        private void PostWebMessage(string message)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
        }

        private void PostMessage(string jsonMessage)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsJson(jsonMessage);
            }
            else
            {
                DebugLogError("PostMessage FAILED: CoreWebView2 is null!");
            }
        }

        protected override void Dispose(bool disposing)
        {
            _isShuttingDown = true;

            if (disposing)
            {
                if (_broker != null)
                {
                    _broker.TasksUpdated -= OnTasksUpdated;
                    _broker.TaskClaimed -= OnTaskClaimed;
                    _broker.TerminalRegistered -= OnTerminalChanged;
                    _broker.TerminalDisconnected -= OnTerminalChanged;
                    _broker.HelperSessionUpdated -= OnHelperSessionUpdated;
                    _broker.ProjectsUpdated -= OnProjectsUpdated;
                    _broker.ReportSaved -= OnReportSaved;
                }

                _webView?.Dispose();
            }

            base.Dispose(disposing);
        }

        // Thin delegate to CodeReviewService — the full sanitize / snapshot /
        // transition logic was extracted to a shared service (task d29512ef
        // item [2]) so CodeReviewPopupForm can call the same code path in-proc.
        // The in-panel JS handler at line ~361 still posts code_review_verdict
        // messages until item [5] removes the in-panel overlay entirely.
        private void HandleCodeReviewVerdict(string taskId, string verdict, string reviewNotesJson = null)
        {
            if (_codeReviewService == null) return;

            var result = _codeReviewService.HandleVerdict(taskId, verdict, reviewNotesJson);
            if (result == null || result.Ok) return;

            if (result.RequiresOperatorAttention && !string.IsNullOrEmpty(result.Error))
            {
                try
                {
                    MessageBox.Show(
                        result.Error,
                        "Code Review — Pass Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                catch (Exception mbEx)
                {
                    DebugLogError($"failed to surface verdict-result dialog: {mbEx.Message}");
                }
            }
            else if (!string.IsNullOrEmpty(result.Error))
            {
                DebugLogError($"Code review verdict error: {result.Error}");
            }
        }
    }
}
