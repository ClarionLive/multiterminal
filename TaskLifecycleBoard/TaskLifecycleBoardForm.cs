using System;
using System.Collections.Generic;
using System.Drawing;
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
using MultiTerminal.Terminal;

namespace MultiTerminal.TaskLifecycleBoard
{
    /// <summary>
    /// Floating window that displays a mini kanban board for a single task's lifecycle.
    /// Replaces the 6-tab modal with 4 fixed workflow columns:
    /// Planning -> Coding -> Testing -> Done
    /// </summary>
    public class TaskLifecycleBoardForm : Form
    {
        private WebView2 _webView;
        private bool _isInitialized;
        private MessageBroker _broker;
        private string _taskId;
        private KanbanTask _task;
        private readonly SettingsService _settings;

        /// <summary>
        /// Track open lifecycle board windows by task ID to prevent duplicates.
        /// </summary>
        private static readonly Dictionary<string, TaskLifecycleBoardForm> _openWindows = new Dictionary<string, TaskLifecycleBoardForm>();

        /// <summary>
        /// Opens or focuses the lifecycle board for a given task.
        /// </summary>
        public static void OpenForTask(string taskId, MessageBroker broker, bool isDarkTheme, SettingsService settings = null)
        {
            if (_openWindows.TryGetValue(taskId, out var existing) && !existing.IsDisposed)
            {
                existing.BringToFront();
                existing.Focus();
                return;
            }

            var form = new TaskLifecycleBoardForm(taskId, broker, isDarkTheme, settings);
            _openWindows[taskId] = form;
            form.Show();
        }

        public TaskLifecycleBoardForm(string taskId, MessageBroker broker, bool isDarkTheme, SettingsService settings = null)
        {
            _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));

            _task = _broker.GetTasks()?.FirstOrDefault(t => t.Id == _taskId);
            if (_task == null)
                throw new InvalidOperationException($"Task not found: {_taskId}");

            // Per design doc: AutoStatus = true for any task opened in the lifecycle board
            if (!_task.AutoStatus)
            {
                _broker.SetAutoStatus(_taskId, true);
            }

            _settings = settings ?? SettingsService.Default;

            InitializeComponent(isDarkTheme);
            RestoreWindowBounds();
            SubscribeToBrokerEvents();
            _ = InitializeWebView2Async();
        }

        private void RestoreWindowBounds()
        {
            var bounds = _settings.GetLifecycleBoardBounds();
            if (bounds.HasValue && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds.Value)))
            {
                StartPosition = FormStartPosition.Manual;
                Left = bounds.Value.Left;
                Top = bounds.Value.Top;
                Width = bounds.Value.Width;
                Height = bounds.Value.Height;
            }
        }

        private void InitializeComponent(bool isDarkTheme)
        {
            // Window settings per design doc: default 1100x700, min 700x500
            Text = $"Lifecycle Board - {_task.Title}";
            Size = new Size(1100, 700);
            MinimumSize = new Size(700, 500);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            Icon = null; // Use default
            ShowInTaskbar = true;
            Font = new Font("Segoe UI", 9f);

            // Dark background to prevent flash
            BackColor = isDarkTheme ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);

            // WebView2 fills the entire form
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
            _webView.WebMessageReceived += OnWebMessageReceived;
            Controls.Add(_webView);
        }

        private void SubscribeToBrokerEvents()
        {
            _broker.TasksUpdated += OnTasksUpdated;
        }

        private void UnsubscribeBrokerEvents()
        {
            if (_broker != null)
            {
                _broker.TasksUpdated -= OnTasksUpdated;
            }
        }

        private void OnTasksUpdated(object sender, List<KanbanTask> tasks)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try { Invoke(new Action(() => OnTasksUpdated(sender, tasks))); }
                catch (ObjectDisposedException) { }
                return;
            }

            // Find our task in the updated list
            var updated = tasks?.FirstOrDefault(t => t.Id == _taskId);
            if (updated != null)
            {
                _task = updated;
                SendTaskToWebView();
            }
        }

        private async System.Threading.Tasks.Task InitializeWebView2Async()
        {
            try
            {
                var env = await WebView2EnvironmentCache.GetEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("TaskLifecycleBoard", $"Lifecycle board WebView2 init failed: {ex.Message}");
            }
        }

        private void OnWebViewInitialized(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess)
            {
                _broker?.DebugLogService?.Error("TaskLifecycleBoard", "Lifecycle board WebView2 initialization failed.");
                return;
            }

            _isInitialized = true;

            // Restore saved zoom level
            var savedZoom = _settings.GetLifecycleBoardZoom();
            _webView.CoreWebView2.Settings.IsPinchZoomEnabled = true;
            _webView.ZoomFactor = savedZoom;

            // Save zoom on change (ctrl+mousewheel)
            _webView.ZoomFactorChanged += (s, args) =>
            {
                _settings.SetLifecycleBoardZoom(_webView.ZoomFactor);
            };

            // Navigate to the lifecycle board HTML
            string htmlPath = GetHtmlPath();
            if (File.Exists(htmlPath))
            {
                _webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
            else
            {
                _broker?.DebugLogService?.Warning("TaskLifecycleBoard", $"Lifecycle board HTML not found: {htmlPath}");
            }
        }

        private string GetHtmlPath()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // Try the standard locations
            string path = Path.Combine(assemblyDir, "TaskLifecycleBoard", "lifecycle-board.html");
            if (File.Exists(path)) return path;

            path = Path.Combine(assemblyDir, "lifecycle-board.html");
            if (File.Exists(path)) return path;

            // Try parent directory (development layout)
            string parentDir = Path.GetDirectoryName(assemblyDir);
            if (parentDir != null)
            {
                path = Path.Combine(parentDir, "TaskLifecycleBoard", "lifecycle-board.html");
                if (File.Exists(path)) return path;
            }

            return Path.Combine(assemblyDir, "TaskLifecycleBoard", "lifecycle-board.html");
        }

        /// <summary>
        /// Send the current task data to the WebView for rendering.
        /// </summary>
        private void SendTaskToWebView()
        {
            if (!_isInitialized || _task == null) return;

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

            // Get projects list for dropdown
            var projectsList = _broker.GetProjectsList();
            var projectsData = projectsList != null
                ? projectsList.Select(p => new { id = p.Id, name = p.Name }).ToArray()
                : Array.Empty<object>();

            var taskJson = JsonSerializer.Serialize(new
            {
                id = _task.Id,
                title = _task.Title,
                description = _task.Description,
                status = _task.Status,
                assignee = _task.Assignee,
                priority = _task.Priority ?? "normal",
                helpers = _task.Helpers ?? new List<string>(),
                checklistJson = _task.ChecklistJson ?? "[]",
                plan = _task.Plan,
                implementationSummary = _task.ImplementationSummary,
                testResults = _task.TestResults,
                continuationNotes = _task.ContinuationNotes,
                projectId = _task.ProjectId,
                subStatus = _task.SubStatus,
                autoStatus = _task.AutoStatus,
                createdBy = _task.CreatedBy,
                createdAt = _task.CreatedAt.ToString("o")
            }, JsonOptions.Unicode);

            // Get attachments for this task
            var attachments = _broker.GetAttachments(_taskId);
            var attachmentsData = attachments.Select(a => new
            {
                id = a.Id,
                checklistItemIndex = a.ChecklistItemIndex,
                fileName = a.FileName,
                mimeType = a.MimeType,
                fileSizeBytes = a.FileSizeBytes,
                addedBy = a.AddedBy,
                createdAt = a.CreatedAt.ToString("o")
            });

            var terminalsJson = JsonSerializer.Serialize(terminalNames, JsonOptions.Unicode);
            var membersJson = JsonSerializer.Serialize(allMembers, JsonOptions.Unicode);
            var projectsJson = JsonSerializer.Serialize(projectsData, JsonOptions.Unicode);
            var attachmentsJson = JsonSerializer.Serialize(attachmentsData, JsonOptions.Unicode);

            PostMessage($"{{\"type\":\"task_data\",\"task\":{taskJson},\"terminals\":{terminalsJson},\"members\":{membersJson},\"projects\":{projectsJson},\"attachments\":{attachmentsJson}}}");
        }

        /// <summary>
        /// Handle messages from the WebView JavaScript.
        /// </summary>
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var messageType = typeElement.GetString();

                switch (messageType)
                {
                    case "ready":
                        SendTaskToWebView();
                        break;

                    case "update_card_status":
                        HandleCardStatusUpdate(root);
                        break;

                    case "update_checklist":
                        HandleChecklistUpdate(root);
                        break;

                    case "update_card_text":
                        HandleCardTextUpdate(root);
                        break;

                    case "add_card":
                        HandleAddCard(root);
                        break;

                    case "delete_card":
                        HandleDeleteCard(root);
                        break;

                    case "update_phase_notes":
                        HandlePhaseNotesUpdate(root);
                        break;

                    case "update_continuation_notes":
                        HandleContinuationNotesUpdate(root);
                        break;

                    case "update_title":
                        HandleTitleUpdate(root);
                        break;

                    case "reorder_cards":
                        HandleReorderCards(root);
                        break;

                    case "update_priority":
                        HandlePriorityUpdate(root);
                        break;

                    case "update_assignee":
                        HandleAssigneeUpdate(root);
                        break;

                    case "update_description":
                        HandleDescriptionUpdate(root);
                        break;

                    case "update_card_assignee":
                        HandleCardAssigneeUpdate(root);
                        break;

                    case "add_card_note":
                        HandleAddCardNote(root);
                        break;

                    case "update_project":
                        HandleProjectUpdate(root);
                        break;

                    case "update_helpers":
                        HandleHelpersUpdate(root);
                        break;

                    case "set_auto_status":
                        HandleSetAutoStatus(root);
                        break;

                    case "move_card":
                        HandleMoveCard(root);
                        break;

                    case "close_form":
                        Close();
                        break;

                    case "add_attachment":
                        HandleAddAttachment(root);
                        break;

                    case "delete_attachment":
                        HandleDeleteAttachment(root);
                        break;
                }
            }
            catch (Exception ex)
            {
                _broker?.DebugLogService?.Error("TaskLifecycleBoard", $"Lifecycle board message error: {ex.Message}");
            }
        }

        private void HandleCardStatusUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("cardIndex", out var indexEl) ||
                !root.TryGetProperty("newStatus", out var statusEl))
                return;

            int cardIndex = indexEl.GetInt32();
            string newStatus = statusEl.GetString();

            var checklist = _task.GetChecklist();
            if (cardIndex < 0 || cardIndex >= checklist.Count) return;

            // Use broker's transition method for proper state machine validation
            _broker?.TransitionChecklistItem(_taskId, cardIndex, newStatus, null, _task.Assignee ?? "User");
        }

        /// <summary>
        /// Atomic card move: validates transition, applies tracking (cycle count, notes),
        /// reorders the checklist, and saves — all in one operation.
        /// Replaces the old dual-message pattern (update_card_status + reorder_cards)
        /// which bypassed the state machine.
        /// </summary>
        private void HandleMoveCard(JsonElement root)
        {
            if (!root.TryGetProperty("cardIndex", out var indexEl) ||
                !root.TryGetProperty("newStatus", out var statusEl) ||
                !root.TryGetProperty("dropPosition", out var posEl))
                return;

            int cardIndex = indexEl.GetInt32();
            string newStatus = statusEl.GetString();
            int dropPosition = posEl.GetInt32();
            string notes = root.TryGetProperty("notes", out var notesEl) ? notesEl.GetString() : null;

            var checklist = _task.GetChecklist();
            if (cardIndex < 0 || cardIndex >= checklist.Count) return;

            var item = checklist[cardIndex];
            var oldStatus = item.Status ?? "pending";

            // If status changed, validate the transition
            if (oldStatus != newStatus)
            {
                var validTransitions = new Dictionary<string, string[]>
                {
                    { "pending", new[] { "coding" } },
                    { "coding", new[] { "testing" } },
                    { "testing", new[] { "coding", "done" } }
                };

                if (!validTransitions.ContainsKey(oldStatus) || !validTransitions[oldStatus].Contains(newStatus))
                {
                    // Invalid transition — force refresh to revert JS state
                    SendTaskToWebView();
                    return;
                }

                // Notes required for transitions from coding or testing
                if ((oldStatus == "coding" || oldStatus == "testing") && string.IsNullOrWhiteSpace(notes))
                {
                    SendTaskToWebView();
                    return;
                }

                // Apply status change
                item.Status = newStatus;
                item.Done = newStatus == "done";

                // Track cycles (testing → coding)
                if (oldStatus == "testing" && newStatus == "coding")
                    item.CycleCount++;

                // Add transition note
                if (item.Notes == null) item.Notes = new List<ChecklistItemNote>();
                if (!string.IsNullOrWhiteSpace(notes))
                {
                    item.Notes.Add(new ChecklistItemNote
                    {
                        By = _task.Assignee ?? "User",
                        At = DateTime.UtcNow.ToString("o"),
                        Transition = $"{oldStatus} → {newStatus}",
                        Text = notes
                    });
                }
            }

            // Reorder: remove item from current position, insert at dropPosition in target column
            checklist.RemoveAt(cardIndex);

            var columns = new Dictionary<string, List<ChecklistItem>>
            {
                { "pending", new List<ChecklistItem>() },
                { "coding", new List<ChecklistItem>() },
                { "testing", new List<ChecklistItem>() },
                { "done", new List<ChecklistItem>() }
            };

            foreach (var ci in checklist)
            {
                var s = ci.Status ?? "pending";
                if (!columns.ContainsKey(s)) s = "pending";
                columns[s].Add(ci);
            }

            // Insert at drop position within target column
            if (dropPosition < 0) dropPosition = 0;
            if (dropPosition > columns[newStatus].Count) dropPosition = columns[newStatus].Count;
            columns[newStatus].Insert(dropPosition, item);

            // Flatten in column order
            var result = new List<ChecklistItem>();
            foreach (var status in new[] { "pending", "coding", "testing", "done" })
                result.AddRange(columns[status]);

            // Save — UpdateTaskChecklist also calls RecalculateAutoStatus
            _task.SetChecklist(result);
            _broker?.UpdateTaskChecklist(_taskId, _task.ChecklistJson);
        }

        private void HandleChecklistUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("checklistJson", out var checklistEl))
                return;

            _broker?.UpdateTaskChecklist(_taskId, checklistEl.GetString());
        }

        private void HandleCardTextUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("cardIndex", out var indexEl) ||
                !root.TryGetProperty("text", out var textEl))
                return;

            int cardIndex = indexEl.GetInt32();
            string newText = textEl.GetString();

            var checklist = _task.GetChecklist();
            if (cardIndex < 0 || cardIndex >= checklist.Count) return;

            checklist[cardIndex].Item = newText;
            _task.SetChecklist(checklist);
            _broker?.UpdateTaskChecklist(_taskId, _task.ChecklistJson);
        }

        private void HandleAddCard(JsonElement root)
        {
            string status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : "pending";
            string text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() : "New item";

            var checklist = _task.GetChecklist();
            var newItem = new ChecklistItem
            {
                Item = text,
                Status = status,
                Done = status == "done",
                Notes = new List<ChecklistItemNote>(),
                CycleCount = 0
            };
            checklist.Add(newItem);
            _task.SetChecklist(checklist);
            _broker?.UpdateTaskChecklist(_taskId, _task.ChecklistJson);
        }

        private void HandleDeleteCard(JsonElement root)
        {
            if (!root.TryGetProperty("cardIndex", out var indexEl))
                return;

            int cardIndex = indexEl.GetInt32();
            var checklist = _task.GetChecklist();
            if (cardIndex < 0 || cardIndex >= checklist.Count) return;

            // Clean up attachments for the deleted checklist item
            _broker?.CleanupChecklistItemAttachments(_taskId, cardIndex);

            checklist.RemoveAt(cardIndex);
            _task.SetChecklist(checklist);
            _broker?.UpdateTaskChecklist(_taskId, _task.ChecklistJson);
        }

        private void HandlePhaseNotesUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("phase", out var phaseEl) ||
                !root.TryGetProperty("notes", out var notesEl))
                return;

            string phase = phaseEl.GetString();
            string notes = notesEl.GetString();

            switch (phase)
            {
                case "planning":
                    _broker?.UpdateTask(_taskId, _task.Title, _task.Description,
                        updatedBy: _task.Assignee ?? "User",
                        plan: notes,
                        implementationSummary: _task.ImplementationSummary,
                        testResults: _task.TestResults);
                    break;
                case "coding":
                    _broker?.UpdateTask(_taskId, _task.Title, _task.Description,
                        updatedBy: _task.Assignee ?? "User",
                        plan: _task.Plan,
                        implementationSummary: notes,
                        testResults: _task.TestResults);
                    break;
                case "testing":
                    _broker?.UpdateTask(_taskId, _task.Title, _task.Description,
                        updatedBy: _task.Assignee ?? "User",
                        plan: _task.Plan,
                        implementationSummary: _task.ImplementationSummary,
                        testResults: notes);
                    break;
            }
        }

        private void HandleContinuationNotesUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("notes", out var notesEl))
                return;

            _broker?.UpdateTaskContinuation(_taskId, notesEl.GetString(), _task.Assignee ?? "User");
        }

        private void HandleTitleUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("title", out var titleEl))
                return;

            _broker?.UpdateTask(_taskId, titleEl.GetString(), _task.Description,
                updatedBy: _task.Assignee ?? "User",
                plan: _task.Plan,
                implementationSummary: _task.ImplementationSummary,
                testResults: _task.TestResults);
        }

        private void HandleReorderCards(JsonElement root)
        {
            if (!root.TryGetProperty("checklistJson", out var checklistEl))
                return;

            _broker?.UpdateTaskChecklist(_taskId, checklistEl.GetString());
        }

        private void HandleSetAutoStatus(JsonElement root)
        {
            if (!root.TryGetProperty("enabled", out var enabledEl))
                return;

            _broker?.SetAutoStatus(_taskId, enabledEl.GetBoolean());
        }

        private void HandleProjectUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("projectId", out var projectEl))
                return;

            // ValueKind guard: a malformed message that sends projectId as a
            // non-string (Number/Array/Object) would throw
            // InvalidOperationException out of GetString() and be silently
            // swallowed by the outer message-pump catch. Reject the shape
            // early with an error toast so the failure is visible. Mirrors
            // the guard in TasksPanelControl.edit_task added in Run 2.
            if (projectEl.ValueKind != JsonValueKind.String
                && projectEl.ValueKind != JsonValueKind.Null)
            {
                var shapePayload = JsonSerializer.Serialize(new
                {
                    type = "error",
                    message = $"Cannot update project: 'projectId' must be a string or null (got {projectEl.ValueKind}).",
                    authoritativeProjectId = _task?.ProjectId
                });
                PostMessage(shapePayload);
                return;
            }

            var result = _broker?.UpdateTaskProject(_taskId, projectEl.GetString());
            if (result?.Success == false)
            {
                // Surface the failure (e.g., ambiguous short id) AND include
                // the authoritative ProjectId so the WebView can roll back
                // its optimistic local mutation. Without authoritativeProjectId
                // the dropdown/header would continue rendering the rejected
                // value until a later refresh — a real state-divergence bug.
                //
                // _task.ProjectId is the post-Run-4-revert value: either the
                // original (when SaveTask threw) or the unchanged stored value
                // (when the broker fast-failed pre-mutation on ambiguous id).
                var payload = JsonSerializer.Serialize(new
                {
                    type = "error",
                    message = result.Error ?? "Failed to update project",
                    authoritativeProjectId = _task?.ProjectId
                });
                PostMessage(payload);
            }
        }

        private void HandleHelpersUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("helpers", out var helpersEl) || helpersEl.ValueKind != JsonValueKind.Array)
                return;

            // Get current helpers to diff
            var currentHelpers = _task.Helpers ?? new List<string>();
            var newHelpers = new List<string>();
            foreach (var h in helpersEl.EnumerateArray())
            {
                var name = h.GetString();
                if (!string.IsNullOrEmpty(name)) newHelpers.Add(name);
            }

            // Add new helpers
            foreach (var helper in newHelpers.Where(h => !currentHelpers.Contains(h, StringComparer.OrdinalIgnoreCase)))
            {
                _ = _broker?.AddHelper(_taskId, helper, _task.Assignee ?? "User");
            }

            // Remove helpers no longer in list
            foreach (var helper in currentHelpers.Where(h => !newHelpers.Contains(h, StringComparer.OrdinalIgnoreCase)))
            {
                _broker?.RemoveHelper(_taskId, helper);
            }
        }

        private void HandlePriorityUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("priority", out var priorityEl))
                return;

            _broker?.UpdateTaskPriority(_taskId, priorityEl.GetString());
        }

        private void HandleAssigneeUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("assignee", out var assigneeEl))
                return;

            _broker?.UpdateTaskAssignee(_taskId, assigneeEl.GetString());
        }

        private void HandleDescriptionUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("description", out var descEl))
                return;

            string description = descEl.GetString();
            _broker?.UpdateTask(_taskId, _task.Title, description,
                updatedBy: _task.Assignee ?? "User",
                plan: _task.Plan,
                implementationSummary: _task.ImplementationSummary,
                testResults: _task.TestResults);
        }

        private void HandleCardAssigneeUpdate(JsonElement root)
        {
            if (!root.TryGetProperty("cardIndex", out var indexEl) ||
                !root.TryGetProperty("assignee", out var assigneeEl))
                return;

            int cardIndex = indexEl.GetInt32();
            string assignee = assigneeEl.GetString();

            var checklist = _task.GetChecklist();
            if (cardIndex < 0 || cardIndex >= checklist.Count) return;

            checklist[cardIndex].AssignedTo = string.IsNullOrEmpty(assignee) ? null : assignee;
            _task.SetChecklist(checklist);
            _broker?.UpdateTaskChecklist(_taskId, _task.ChecklistJson);
        }

        private void HandleAddCardNote(JsonElement root)
        {
            if (!root.TryGetProperty("cardIndex", out var indexEl) ||
                !root.TryGetProperty("text", out var textEl))
                return;

            int cardIndex = indexEl.GetInt32();
            string text = textEl.GetString();
            string by = root.TryGetProperty("by", out var byEl) ? byEl.GetString() : (_task.Assignee ?? "User");

            var checklist = _task.GetChecklist();
            if (cardIndex < 0 || cardIndex >= checklist.Count) return;

            if (checklist[cardIndex].Notes == null)
                checklist[cardIndex].Notes = new List<ChecklistItemNote>();

            checklist[cardIndex].Notes.Add(new ChecklistItemNote
            {
                By = by,
                At = DateTime.UtcNow.ToString("o"),
                Transition = null,
                Text = text
            });

            _task.SetChecklist(checklist);
            _broker?.UpdateTaskChecklist(_taskId, _task.ChecklistJson);
        }

        private void HandleAddAttachment(JsonElement root)
        {
            if (!root.TryGetProperty("cardIndex", out var indexEl) ||
                !root.TryGetProperty("fileName", out var fileNameEl) ||
                !root.TryGetProperty("mimeType", out var mimeTypeEl) ||
                !root.TryGetProperty("dataBase64", out var dataEl))
                return;

            int cardIndex = indexEl.GetInt32();
            string fileName = fileNameEl.GetString();
            string mimeType = mimeTypeEl.GetString();
            string dataBase64 = dataEl.GetString();

            byte[] imageBytes;
            try
            {
                imageBytes = Convert.FromBase64String(dataBase64);
            }
            catch (FormatException)
            {
                _broker?.DebugLogService?.Warning("TaskLifecycleBoard", "Lifecycle board: invalid base64 data for attachment");
                return;
            }

            var result = _broker?.AddAttachment(_taskId, cardIndex, fileName, mimeType, imageBytes, "user");
            if (result?.Success == true)
            {
                SendAttachmentsToWebView();
            }
        }

        private void HandleDeleteAttachment(JsonElement root)
        {
            if (!root.TryGetProperty("attachmentId", out var idEl))
                return;

            string attachmentId = idEl.GetString();

            var result = _broker?.DeleteAttachment(attachmentId);
            if (result?.Success == true)
            {
                SendAttachmentsToWebView();
            }
        }

        /// <summary>
        /// Send the current attachment list for the task to the WebView.
        /// </summary>
        private void SendAttachmentsToWebView()
        {
            if (!_isInitialized || _broker == null) return;

            var attachments = _broker.GetAttachments(_taskId);
            var attachmentsJson = JsonSerializer.Serialize(attachments.Select(a => new
            {
                id = a.Id,
                checklistItemIndex = a.ChecklistItemIndex,
                fileName = a.FileName,
                mimeType = a.MimeType,
                fileSizeBytes = a.FileSizeBytes,
                addedBy = a.AddedBy,
                createdAt = a.CreatedAt.ToString("o")
            }), JsonOptions.Unicode);

            PostMessage($"{{\"type\":\"attachments_updated\",\"attachments\":{attachmentsJson}}}");
        }

        private void PostMessage(string jsonMessage)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsJson(jsonMessage);
            }
        }

        private void PostWebMessage(string message)
        {
            if (_webView?.CoreWebView2 != null)
            {
                _webView.CoreWebView2.PostWebMessageAsString(message);
            }
        }

        /// <summary>
        /// Apply theme to the board.
        /// </summary>
        public void ApplyTheme(bool isDark)
        {
            BackColor = isDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(245, 245, 245);
            if (!_isInitialized) return;
            PostWebMessage($"theme:{(isDark ? "dark" : "light")}");
        }

        /// <summary>
        /// Apply theme to all open lifecycle board windows.
        /// Called from MainForm when the user toggles the theme.
        /// </summary>
        public static void ApplyThemeToAll(bool isDark)
        {
            foreach (var kvp in _openWindows)
            {
                if (kvp.Value != null && !kvp.Value.IsDisposed)
                {
                    kvp.Value.ApplyTheme(isDark);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Save window bounds for next use
            if (WindowState == FormWindowState.Normal)
                _settings.SetLifecycleBoardBounds(new Rectangle(Left, Top, Width, Height));
            else
                _settings.SetLifecycleBoardBounds(RestoreBounds);

            UnsubscribeBrokerEvents();
            _openWindows.Remove(_taskId);

            if (_webView != null)
            {
                _webView.CoreWebView2InitializationCompleted -= OnWebViewInitialized;
                _webView.WebMessageReceived -= OnWebMessageReceived;
                _webView.Dispose();
                _webView = null;
            }

            base.OnFormClosed(e);
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
