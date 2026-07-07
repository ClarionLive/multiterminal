using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Owns the Kanban-task cache and all task CRUD, extracted from <see cref="MessageBroker"/> in ticket
    /// e7e89f4b (proof-of-pattern region extraction). The single write path (clone→persist→swap-on-success,
    /// P5 / 1df2a534) lives here now: <see cref="MutateTaskInternal"/> / <see cref="TryMutateTask"/> /
    /// <see cref="InsertTaskInternal"/> / <see cref="DeleteTaskInternal"/>. Everything a task method needs
    /// from another region — event raising, activity/inbox/notifications, project resolution, worktree
    /// lifecycle, attachments — is reached through <see cref="ITaskServiceHost"/>, which the broker
    /// implements. The broker keeps its full public task surface as one-line delegations to this service,
    /// so callers (controllers, panels, MainForm) are untouched.
    /// </summary>
    internal sealed class TaskService
    {
        // Task cache — the ownership this extraction is about. Moved verbatim from MessageBroker.
        private readonly ConcurrentDictionary<string, KanbanTask> _tasks = new ConcurrentDictionary<string, KanbanTask>();

        // Serializes checklist read-modify-write across all checklist mutators (append, full-replace,
        // transition, assign). _tasks being concurrent only guards the lookup, not the per-task
        // GetChecklist()->mutate->SetChecklist()->SaveTask() sequence, which is otherwise last-writer-wins.
        private readonly object _checklistMutationLock = new object();

        private readonly TaskDatabase _taskDb;
        private readonly ITaskServiceHost _host;

        public TaskService(TaskDatabase taskDb, ITaskServiceHost host)
        {
            _taskDb = taskDb ?? throw new ArgumentNullException(nameof(taskDb));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Load tasks from the database into memory. Startup bootstrap seed (write-path bypass, P5): populate
        /// the cache directly from the DB, no per-row broadcast, no persist-back. Called from the broker ctor.
        /// </summary>
        public void LoadPersistedTasks()
        {
            try
            {
                var tasks = _taskDb.LoadAllTasks();
                foreach (var task in tasks)
                {
                    _tasks.TryAdd(task.Id, task);
                }
                _host.LogInfo($"Loaded {tasks.Count} tasks from database");
            }
            catch (Exception ex)
            {
                _host.LogError($"Failed to load tasks: {ex.Message}");
            }
        }


        /// <summary>
        /// Get a task by ID from the in-memory cache.
        /// </summary>
        public KanbanTask GetTask(string taskId)
        {
            return _tasks.TryGetValue(taskId, out var task) ? task : null;
        }


        /// <summary>
        /// Save a task object to the database and broadcast the update. Use when you've built or modified
        /// a task's properties directly and want it persisted as the current state.
        /// <para>Write-path ordering (P5 / 1df2a534): persist FIRST, then make the cache authoritative for
        /// this task id and broadcast — but ONLY on persist success. Pre-P5 this always broadcast even when
        /// the DB write threw (announcing a change that never landed) and never updated the cache (it
        /// assumed the caller had mutated the cached reference in place). Now a persist failure is logged
        /// and the cache/listeners are left untouched.</para>
        /// </summary>
        public void SaveTask(KanbanTask task)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                _taskDb.SaveTask(task);   // persist FIRST
            }
            catch (Exception ex)
            {
                // Don't swap the cache or broadcast a change we couldn't commit.
                _host.LogError($"SaveTask: persist failed for task {task.Id}: {ex.Message}");
                return;
            }

            _tasks[task.Id] = task;   // cache reflects exactly what was persisted
            BroadcastTaskUpdate();
        }


        // Persists an agent report and fires ReportSaved so the kanban card
        // badges refresh. Used by TasksPanelControl when the human reviewer
        // hits Pass — we snapshot the cleared review_notes block to task_reports
        // before nulling so the audit trail survives (task 87ee90c3 F5).
        public string SaveTaskReport(string taskId, string agentName, string reportType, string reportContent, string verdict, int? score, string createdBy)
        {
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                _taskDb.SaveTaskReport(id, taskId, null, agentName, reportType ?? "markdown", reportContent, verdict, score, createdBy);
                _host.NotifyReportSaved(taskId, id, agentName, verdict);
                return id;
            }
            catch (Exception ex)
            {
                _host.LogError($"SaveTaskReport failed: {ex.Message}");
                return null;
            }
        }


        // REST data-access pass-throughs (task 7ce19175: TaskReportsController routes DB access
        // through the broker per API/CONVENTIONS.md). Thin wrappers over TaskDb.

        /// <summary>
        /// Persist an agent report with a caller-supplied id (+ optional invocationId) and fire
        /// ReportSaved so kanban badges refresh. Overload for the REST TaskReportsController, which
        /// owns id generation; the id-less overload above auto-generates the id for in-app callers.
        /// </summary>
        public void SaveTaskReport(string id, string taskId, string invocationId, string agentName, string reportType, string reportContent, string verdict, int? score, string createdBy)
        {
            _taskDb.SaveTaskReport(id, taskId, invocationId, agentName, reportType, reportContent, verdict, score, createdBy);
            _host.NotifyReportSaved(taskId, id, agentName, verdict);
        }


        /// <summary>List agent reports for a task (metadata only, no content).</summary>
        public List<Dictionary<string, object>> GetTaskReports(string taskId, string agentName = null, int limit = 50)
            => _taskDb.GetTaskReports(taskId, agentName, limit);


        /// <summary>Get a single agent report by id (full content).</summary>
        public Dictionary<string, object> GetTaskReport(string reportId) => _taskDb.GetTaskReport(reportId);


        /// <summary>Load helper assignments for a task.</summary>
        public List<TaskHelper> LoadTaskHelpers(string taskId) => _taskDb.LoadTaskHelpers(taskId);


        /// <summary>
        /// Create a new task on the Kanban board.
        /// </summary>
        public CreateTaskResult CreateTask(string title, string description, string createdBy, string status = "todo", string priority = "normal", string projectId = null)
        {
            var validStatuses = new[] { "todo", "in_progress", "done", "suggestion" };
            if (!validStatuses.Contains(status))
                status = "todo";

            // Validate priority
            var validPriorities = new[] { "urgent", "normal", "low" };
            if (!validPriorities.Contains(priority))
                priority = "normal";

            // Distinguish "no project" (empty input — caller intent) from
            // "ambiguous project id" (NormalizeProjectId returned null because
            // a short input matched a key that ALSO prefixes longer keys).
            // Failing fast on ambiguity prevents the call from looking like a
            // success while silently dropping the requested project.
            string canonicalProjectId = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                canonicalProjectId = _host.NormalizeProjectId(projectId);
                if (canonicalProjectId == null)
                {
                    return new CreateTaskResult
                    {
                        Success = false,
                        Error = $"Project id '{projectId}' is ambiguous (matches multiple registered projects, or is a short prefix of one). Pass the full id."
                    };
                }
            }

            var task = new KanbanTask
            {
                Title = title,
                Description = description,
                CreatedBy = createdBy,
                Status = status,
                Priority = priority,
                ProjectId = canonicalProjectId,
                // Seed sort_order at the end of the target column so new tasks
                // land at the bottom — matches typical kanban UX (new work
                // appears at the bottom of To Do, not the top).
                SortOrder = _taskDb.GetNextSortOrderForStatus(status)
            };

            // Persist-before-cache (write path): if the durable write fails, the task never enters the
            // cache — callers can't observe a task that won't survive a restart — we skip the broadcast/
            // activity announcements (they'd advertise a row that doesn't exist), and return Success=false
            // so the caller-side toast plumbing fires.
            try
            {
                InsertTaskInternal(task);
            }
            catch (Exception ex)
            {
                _host.LogError($"CreateTask: failed to persist new task: {ex.Message}");
                return new CreateTaskResult { Success = false, Error = $"Failed to persist new task: {ex.Message}" };
            }

            BroadcastTaskUpdate();

            // Record activity for the feed
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = createdBy ?? "System",
                Type = "task",
                Action = "created",
                Content = $"Created task: {title}",
                RelatedId = task.Id
            });

            // Update office panel activity
            if (!string.IsNullOrEmpty(createdBy))
                _host.ActivityService?.UpdateActivity(createdBy, "working", $"Created task: {title}");

            return new CreateTaskResult { Success = true, TaskId = task.Id };
        }


        /// <summary>
        /// Create a quick-task — a lightweight, immutable attribution anchor for trivial
        /// working-tree changes that don't warrant a full kanban card. Always status='done',
        /// no checklist, no plan. After creation only the title can be edited (via
        /// <see cref="UpdateQuickTaskTitle"/>); all other mutation methods reject the task.
        /// Hidden by default from list_tasks (controller-level filter). See task d42423e3.
        /// </summary>
        public CreateTaskResult CreateQuickTask(string title, string createdBy, string projectId = null)
        {
            if (string.IsNullOrWhiteSpace(title))
                return new CreateTaskResult { Success = false, Error = "Title required" };

            string canonicalProjectId = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                canonicalProjectId = _host.NormalizeProjectId(projectId);
                if (canonicalProjectId == null)
                {
                    return new CreateTaskResult
                    {
                        Success = false,
                        Error = $"Project id '{projectId}' is ambiguous (matches multiple registered projects, or is a short prefix of one). Pass the full id."
                    };
                }
            }

            var task = new KanbanTask
            {
                Title = title,
                Description = null,
                CreatedBy = createdBy,
                Status = "done",
                Priority = "normal",
                ProjectId = canonicalProjectId,
                IsQuickTask = true,
                ChecklistJson = "[]",
                ImplementationChecklistJson = "[]",
                SortOrder = _taskDb.GetNextSortOrderForStatus("done")
            };

            // Persist-before-cache (write path): on a durable-write failure the quick-task never enters
            // the cache, we skip the broadcast/activity, and return Success=false.
            try
            {
                InsertTaskInternal(task);
            }
            catch (Exception ex)
            {
                _host.LogError($"CreateQuickTask: failed to persist quick task: {ex.Message}");
                return new CreateTaskResult { Success = false, Error = $"Failed to persist quick task: {ex.Message}" };
            }

            BroadcastTaskUpdate();

            _host.RecordActivity(new ActivityEvent
            {
                Terminal = createdBy ?? "System",
                Type = "task",
                Action = "quick_created",
                Content = $"Quick task: {title}",
                RelatedId = task.Id
            });

            return new CreateTaskResult { Success = true, TaskId = task.Id };
        }


        /// <summary>
        /// Update the title of a quick-task. The only mutation allowed on quick-tasks
        /// (per the immutability contract — see <see cref="CreateQuickTask"/>).
        /// Returns Success=false if the task isn't a quick-task or doesn't exist.
        /// </summary>
        public UpdateTaskResult UpdateQuickTaskTitle(string taskId, string newTitle, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };

            if (!task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Task {taskId} is not a quick-task. Use UpdateTask for regular tasks." };

            if (string.IsNullOrWhiteSpace(newTitle))
                return new UpdateTaskResult { Success = false, Error = "Title cannot be empty" };

            var previousTitle = task.Title;
            var assignee = task.Assignee;

            if (!TryMutateTask(taskId, t => t.Title = newTitle))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist quick task title" };
            }

            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "edited",
                Content = $"Renamed quick task: '{previousTitle}' → '{newTitle}'",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Claim a task by assigning it to a terminal.
        /// Priority-aware stack behavior based on task's priority field:
        /// - urgent: Pauses current active task (unless in critical section), makes this task active
        /// - normal: Queues behind current active task (default)
        /// - low: Added to bottom of paused stack
        /// </summary>
        /// <param name="taskId">The task ID to claim</param>
        /// <param name="assignee">The terminal name claiming the task</param>
        /// <param name="priorityOverride">Optional priority override. If specified, uses this instead of task's priority.</param>
        public ClaimTaskResult ClaimTask(string taskId, string assignee, string priorityOverride = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new ClaimTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Allow re-claiming by the same person, but block if claimed by someone else
            if (!string.IsNullOrEmpty(task.Assignee) && !task.Assignee.Equals(assignee, StringComparison.OrdinalIgnoreCase))
            {
                return new ClaimTaskResult { Success = false, Error = $"Task already claimed by {task.Assignee}" };
            }

            // Use priority override if specified, otherwise use task's priority
            var validPriorities = new[] { "urgent", "normal", "low" };
            var priority = !string.IsNullOrWhiteSpace(priorityOverride) && validPriorities.Contains(priorityOverride)
                ? priorityOverride
                : (task.Priority ?? "normal");

            // Find current active task for this assignee
            var currentActiveTask = _tasks.Values.FirstOrDefault(t =>
                t.Assignee != null &&
                t.Assignee.Equals(assignee, StringComparison.OrdinalIgnoreCase) &&
                t.SubStatus == "active" &&
                t.Id != taskId);

            // Handle based on priority
            bool wasQueued = false;
            string queuedBehind = null;

            // The CORE claim persist (queue or activate the CLAIMED task) is FATAL: if the durable write
            // fails we abort with Success=false and do NOT broadcast, so an agent never proceeds on a claim
            // that evaporates on restart (P5 pipeline A). Pausing the sibling is best-effort.
            const string claimPersistFailed = "Failed to persist the claim (the task's durable state was not written — DB unavailable?).";

            if (currentActiveTask != null)
            {
                switch (priority)
                {
                    case "urgent":
                        // Check if terminal is in critical section
                        if (IsTerminalInCriticalSection(assignee))
                        {
                            // Queue the urgent task - it will activate when critical section ends
                            if (!QueueTaskForTerminal(task, assignee, isLowPriority: false))
                            {
                                return new ClaimTaskResult { Success = false, Error = claimPersistFailed };
                            }

                            wasQueued = true;
                            queuedBehind = currentActiveTask.Title;

                            _host.RecordActivity(new ActivityEvent
                            {
                                Terminal = assignee,
                                Type = "task",
                                Action = "queued",
                                Content = $"Urgent task queued (terminal in critical section): {task.Title}",
                                RelatedId = taskId
                            });
                        }
                        else
                        {
                            // Activate the urgent task FIRST (fatal), THEN pause the current active task
                            // (best-effort). This ordering means a failed activation leaves the sibling
                            // unpaused and coherent (P5 pipeline A, SetTaskActive ruling applied here too).
                            if (!MakeTaskActive(task, assignee, taskId))
                            {
                                return new ClaimTaskResult { Success = false, Error = claimPersistFailed };
                            }

                            PauseTaskWithSummary(currentActiveTask, assignee);
                        }
                        break;

                    case "low":
                        // Add to bottom of paused stack
                        if (!QueueTaskForTerminal(task, assignee, isLowPriority: true))
                        {
                            return new ClaimTaskResult { Success = false, Error = claimPersistFailed };
                        }

                        wasQueued = true;
                        queuedBehind = currentActiveTask.Title;

                        _host.RecordActivity(new ActivityEvent
                        {
                            Terminal = assignee,
                            Type = "task",
                            Action = "queued",
                            Content = $"Low priority task queued: {task.Title}",
                            RelatedId = taskId
                        });
                        break;

                    case "normal":
                    default:
                        // Queue behind current task (doesn't interrupt)
                        if (!QueueTaskForTerminal(task, assignee, isLowPriority: false))
                        {
                            return new ClaimTaskResult { Success = false, Error = claimPersistFailed };
                        }

                        wasQueued = true;
                        queuedBehind = currentActiveTask.Title;

                        _host.RecordActivity(new ActivityEvent
                        {
                            Terminal = assignee,
                            Type = "task",
                            Action = "queued",
                            Content = $"Task queued behind active work: {task.Title}",
                            RelatedId = taskId
                        });
                        break;
                }
            }
            else
            {
                // No active task - make this task active regardless of priority
                if (!MakeTaskActive(task, assignee, taskId))
                {
                    return new ClaimTaskResult { Success = false, Error = claimPersistFailed };
                }
            }

            BroadcastTaskUpdate();

            // Raise TaskClaimed event for toast notification
            _host.RaiseTaskClaimed(new TaskClaimedEventArgs
            {
                TaskId = taskId,
                TaskTitle = task.Title,
                ClaimedBy = assignee
            });

            // Analyze complexity and include suggestion if warranted
            ComplexitySuggestion complexitySuggestion = null;
            if (_host.ComplexityDetector != null)
            {
                try
                {
                    var complexityResult = _host.ComplexityDetector.Analyze(task.Title, task.Description);
                    if (complexityResult.SuggestPlan)
                    {
                        complexitySuggestion = new ComplexitySuggestion
                        {
                            Score = complexityResult.Score,
                            SuggestPlan = complexityResult.SuggestPlan,
                            Signals = complexityResult.Signals,
                            Recommendation = complexityResult.Recommendation
                        };
                    }
                }
                catch (Exception ex)
                {
                    _host.LogError($"Complexity analysis failed: {ex.Message}");
                }
            }

            return new ClaimTaskResult
            {
                Success = true,
                ComplexitySuggestion = complexitySuggestion,
                WasQueued = wasQueued,
                QueuedBehind = queuedBehind
            };
        }


        private bool IsTerminalInCriticalSection(string terminalName)
        {
            var activity = _taskDb.GetTerminalActivity(terminalName);
            if (activity == null) return false;

            // Check if in critical section AND timeout hasn't expired
            if (!activity.InCriticalSection) return false;
            if (!activity.CriticalSectionTimeout.HasValue) return false;

            // Return true only if the timeout is still in the future
            return activity.CriticalSectionTimeout.Value > DateTime.UtcNow;
        }


        /// <summary>
        /// Queue a task for a terminal (add to paused stack without interrupting current work).
        /// </summary>
        /// <returns>true if the claim persisted; false (not found OR DB write failed) so ClaimTask aborts
        /// with Success=false. The core claim persist is FATAL (P5 pipeline A); summary is best-effort.</returns>
        private bool QueueTaskForTerminal(KanbanTask task, string assignee, bool isLowPriority = false)
        {
            // Core claim persist through the write path, no broadcast — the ClaimTask caller broadcasts
            // once after the whole claim. Low priority tasks get a past timestamp to sort to the bottom.
            try
            {
                if (MutateTaskInternal(task.Id, t =>
                {
                    t.Assignee = assignee;
                    t.Status = "in_progress";
                    t.SubStatus = "queued";
                    t.PausedAt = isLowPriority ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow;
                }) == null)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _host.LogError($"QueueTaskForTerminal: persist failed for {task.Id}: {ex.Message}");
                return false;
            }

            // Auto-generate summary when task is claimed (even if queued)
            if (_host.SummaryService != null)
            {
                try
                {
                    _host.SummaryService.AutoGenerateSummary(
                        task.Id,
                        task.Title,
                        "todo",
                        "in_progress",
                        assignee);
                }
                catch (Exception ex)
                {
                    _host.LogError($"Failed to auto-generate summary: {ex.Message}");
                }
            }

            return true;
        }


        /// <summary>
        /// Pause a task with auto-generated summary.
        /// </summary>
        private void PauseTaskWithSummary(KanbanTask task, string assignee)
        {
            // Write path, no broadcast — the ClaimTask caller broadcasts once after the whole claim.
            try
            {
                MutateTaskInternal(task.Id, t =>
                {
                    t.SubStatus = "paused";
                    t.PausedAt = DateTime.UtcNow;
                });
            }
            catch (Exception ex) { _host.LogError($"PauseTaskWithSummary: persist failed for {task.Id}: {ex.Message}"); }

            // Auto-generate summary for the paused task
            if (_host.SummaryService != null)
            {
                try
                {
                    _host.SummaryService.AutoGenerateSummary(
                        task.Id,
                        task.Title,
                        "in_progress",
                        "paused",
                        assignee);
                }
                catch (Exception ex)
                {
                    _host.LogError($"Failed to auto-generate summary for paused task: {ex.Message}");
                }
            }

            // Record activity for pausing
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = assignee,
                Type = "task",
                Action = "paused",
                Content = $"Paused task: {task.Title}",
                RelatedId = task.Id
            });
        }


        /// <summary>
        /// Make a task active and update activity tracking.
        /// </summary>
        /// <returns>true if the activation persisted; false (not found OR DB write failed) so ClaimTask
        /// aborts with Success=false. The core activation persist is FATAL (P5 pipeline A).</returns>
        private bool MakeTaskActive(KanbanTask task, string assignee, string taskId)
        {
            // Core activation persist through the write path, no broadcast — ClaimTask broadcasts once.
            try
            {
                if (MutateTaskInternal(taskId, t =>
                {
                    t.Assignee = assignee;
                    t.Status = "in_progress";
                    t.SubStatus = "active";
                }) == null)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _host.LogError($"MakeTaskActive: persist failed for {taskId}: {ex.Message}");
                return false;
            }

            // Auto-generate summary when task is claimed
            if (_host.SummaryService != null)
            {
                try
                {
                    _host.SummaryService.AutoGenerateSummary(
                        taskId,
                        task.Title,
                        "todo",
                        "in_progress",
                        assignee);
                }
                catch (Exception ex)
                {
                    _host.LogError($"Failed to auto-generate summary: {ex.Message}");
                }
            }

            // Auto-update activity: terminal claimed a task
            _host.ActivityService?.UpdateActivity(
                assignee,
                "working",
                $"Working on: {task.Title}",
                taskId: taskId);

            // Record activity for the feed
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = assignee,
                Type = "task",
                Action = "claimed",
                Content = $"Claimed task: {task.Title}",
                RelatedId = taskId
            });

            return true;
        }


        /// <summary>
        /// Get the most recently paused or queued task for an assignee.
        /// Tasks with SubStatus "paused" or "queued" are candidates for auto-resume.
        /// Ordered by PausedAt descending (most recent first), which means low-priority
        /// tasks (with older timestamps) are at the bottom of the stack.
        /// </summary>
        private KanbanTask GetMostRecentPausedTask(string assignee)
        {
            return _tasks.Values
                .Where(t => t.Assignee != null &&
                            t.Assignee.Equals(assignee, StringComparison.OrdinalIgnoreCase) &&
                            (t.SubStatus == "paused" || t.SubStatus == "queued"))
                .OrderByDescending(t => t.PausedAt)
                .FirstOrDefault();
        }


        /// <summary>
        /// Update the status of a task.
        /// Implements stack behavior: when a task is marked "done" and was active,
        /// auto-resume the most recently paused task for that assignee.
        /// </summary>
        public UpdateTaskStatusResult UpdateTaskStatus(string taskId, string status)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskStatusResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskStatusResult { Success = false, Error = $"Cannot change status: task {taskId} is a quick-task (immutable; status is permanently 'done')." };

            var validStatuses = new[] { "todo", "in_progress", "done", "suggestion" };
            if (!validStatuses.Contains(status))
            {
                return new UpdateTaskStatusResult { Success = false, Error = $"Invalid status: {status}" };
            }

            var previousStatus = task.Status;
            var previousSubStatus = task.SubStatus;
            var assignee = task.Assignee;

            // Write path (no broadcast — broadcast once below, after the optional auto-resume). The status
            // persist is FATAL (P5 pipeline A): failure returns Success=false with no broadcast/side-effects
            // and the cache untouched, so a caller never proceeds on a status change that didn't persist.
            try
            {
                if (MutateTaskInternal(taskId, t =>
                {
                    t.Status = status;

                    // Manual override: if user explicitly sets status while AutoStatus is on, disable
                    // auto-status so we respect their choice.
                    if (t.AutoStatus)
                    {
                        t.AutoStatus = false;
                    }

                    // Clear SubStatus when task is done.
                    if (status == "done")
                    {
                        t.SubStatus = null;
                        t.PausedAt = null;
                    }

                    // Clear assignee and SubStatus if moving back to todo or suggestion.
                    if (status == "todo" || status == "suggestion")
                    {
                        t.Assignee = null;
                        t.SubStatus = null;
                        t.PausedAt = null;
                    }
                }) == null)
                {
                    return new UpdateTaskStatusResult { Success = false, Error = $"Task not found: {taskId}" };
                }
            }
            catch (Exception ex)
            {
                _host.LogError($"UpdateTaskStatus: persist failed for {taskId}: {ex.Message}");
                return new UpdateTaskStatusResult { Success = false, Error = $"Failed to persist status change: {ex.Message}" };
            }

            // Auto-generate summary on status change
            if (previousStatus != status && _host.SummaryService != null)
            {
                try
                {
                    _host.SummaryService.AutoGenerateSummary(
                        taskId,
                        task.Title,
                        previousStatus,
                        status,
                        assignee ?? "System");
                }
                catch (Exception ex)
                {
                    _host.LogError($"Failed to auto-generate summary: {ex.Message}");
                }
            }

            // Stack behavior: auto-resume most recently paused task when active task is marked done
            KanbanTask resumedTask = null;
            if (status == "done" && previousSubStatus == "active" && !string.IsNullOrEmpty(assignee))
            {
                resumedTask = GetMostRecentPausedTask(assignee);
                if (resumedTask != null)
                {
                    // Write path for the auto-resumed task (no broadcast — one broadcast below covers both).
                    try
                    {
                        MutateTaskInternal(resumedTask.Id, t =>
                        {
                            t.SubStatus = "active";
                            t.PausedAt = null;
                        });
                    }
                    catch (Exception ex) { _host.LogError($"UpdateTaskStatus: persist failed for resumed task {resumedTask.Id}: {ex.Message}"); }

                    // Update activity to show the resumed task
                    _host.ActivityService?.UpdateActivity(
                        assignee,
                        "working",
                        $"Working on: {resumedTask.Title}",
                        taskId: resumedTask.Id);

                    // Record activity for resuming
                    _host.RecordActivity(new ActivityEvent
                    {
                        Terminal = assignee,
                        Type = "task",
                        Action = "resumed",
                        Content = $"Resumed task: {resumedTask.Title}",
                        RelatedId = resumedTask.Id
                    });
                }
            }

            BroadcastTaskUpdate();

            // Auto-update activity when task is marked done
            if (status == "done" && !string.IsNullOrEmpty(assignee))
            {
                // Only set to idle if no task was auto-resumed
                if (resumedTask == null)
                {
                    _host.ActivityService?.UpdateActivity(
                        assignee,
                        "idle",
                        $"Completed: {task.Title}",
                        taskId: null);  // Clear task association
                }

                // Record activity for the feed
                _host.RecordActivity(new ActivityEvent
                {
                    Terminal = assignee,
                    Type = "task",
                    Action = "completed",
                    Content = $"Completed task: {task.Title}",
                    RelatedId = taskId
                });
            }
            else if (status != previousStatus)
            {
                // Record other status changes
                _host.RecordActivity(new ActivityEvent
                {
                    Terminal = assignee ?? "System",
                    Type = "task",
                    Action = "updated",
                    Content = $"Task '{task.Title}' → {status}",
                    RelatedId = taskId
                });
            }

            // Phase 2 worktree commit-then-prune — gated by MULTITERMINAL_WORKTREE_MODE.
            // Auto-commit any changes in the worktree FIRST so the prune step
            // (which doesn't use --force) doesn't refuse on dirty state. If the
            // commit fails, skip prune so the dev can resolve manually; the DB
            // record stays 'active' for the next attempt.
            // Pre-declared (not inline-out) so it stays definitely-assigned for the
            // ineligible-completion else-if below: with the short-circuit && the
            // predicate isn't called when status != "done", so an inline out var
            // wouldn't be assigned on every path into the else.
            string doneSkipReason = null;
            if (status == "done"
                && _host.TryResolveWorktreeEligibility(task, out string doneProjPath, out _, out doneSkipReason))
            {
                bool shouldPrune = false;
                MultiTerminal.Services.AutoCommitResult commitResult = null;
                try
                {
                    commitResult = _host.AutoCommit.CommitForTaskAsync(
                        taskId,
                        doneProjPath,
                        task.Title,
                        task.ImplementationSummary,
                        task.Assignee).GetAwaiter().GetResult();

                    if (commitResult.Success)
                    {
                        shouldPrune = true;
                        _host.LogWarning($"Auto-commit for {taskId}: " +
                            (commitResult.SkippedReason ?? $"committed {commitResult.CommitHash}"));

                        if (!string.IsNullOrEmpty(commitResult.SkippedReason))
                        {
                            _host.RecordActivity(new ActivityEvent
                            {
                                Terminal = task.Assignee ?? "System",
                                Type = "worktree",
                                Action = "auto_commit_skipped",
                                Content = $"Worktree for '{task.Title}' had no changes to commit ({commitResult.SkippedReason}).",
                                RelatedId = taskId
                            });
                        }
                        else
                        {
                            string shortHash = !string.IsNullOrEmpty(commitResult.CommitHash) && commitResult.CommitHash.Length >= 7
                                ? commitResult.CommitHash.Substring(0, 7)
                                : commitResult.CommitHash;
                            int fileCount = commitResult.ChangedFiles?.Count ?? 0;
                            string unlinkedNote = (commitResult.UnlinkedFiles != null && commitResult.UnlinkedFiles.Count > 0)
                                ? $" {commitResult.UnlinkedFiles.Count} of those weren't pre-linked via link_task_file: {string.Join(", ", commitResult.UnlinkedFiles.Take(5))}{(commitResult.UnlinkedFiles.Count > 5 ? ", ..." : "")}"
                                : "";
                            _host.RecordActivity(new ActivityEvent
                            {
                                Terminal = task.Assignee ?? "System",
                                Type = "worktree",
                                Action = "auto_commit",
                                Content = $"Auto-committed {fileCount} file(s) as {shortHash} for '{task.Title}'.{unlinkedNote}",
                                RelatedId = taskId
                            });
                        }
                    }
                    else
                    {
                        _host.LogError($"Auto-commit for {taskId} FAILED: {commitResult.Stderr}");
                        _host.RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "auto_commit_failed",
                            Content = $"Auto-commit failed for '{task.Title}'. Worktree NOT pruned — see debug log for details.",
                            RelatedId = taskId
                        });
                    }
                }
                catch (Exception ex)
                {
                    _host.LogError($"Auto-commit threw for task {taskId}: {ex.Message}");
                    // Activity feed is broadcast to HUD/board/MCP clients; raw
                    // exception text often leaks absolute paths, branch names,
                    // or git command details. Keep the full message in the
                    // server-side debug log above (LogError) and surface a
                    // generic notice to clients.
                    _host.RecordActivity(new ActivityEvent
                    {
                        Terminal = task.Assignee ?? "System",
                        Type = "worktree",
                        Action = "auto_commit_failed",
                        Content = $"Auto-commit threw for '{task.Title}'. Worktree NOT pruned — see debug log for details.",
                        RelatedId = taskId
                    });
                }

                // Phase 2 (helpers) + Phase 2.5 (integration) for per-agent isolation:
                // commit every helper worktree and merge each helper branch into the
                // canonical branch BEFORE pruning. A helper-commit failure or a merge
                // conflict halts teardown (shouldPrune=false) so nothing is lost.
                // No-op for single-agent tasks (the canonical commit already ran).
                List<string> integratedHelperBranches = new List<string>();
                if (shouldPrune)
                {
                    bool proceedTeardown;
                    try
                    {
                        proceedTeardown = _host.CommitAndIntegrateHelpers(task, doneProjPath, out integratedHelperBranches);
                    }
                    catch (Exception ex)
                    {
                        _host.LogError($"CommitAndIntegrateHelpers threw for {taskId}: {ex.Message}");
                        proceedTeardown = false;
                    }
                    if (!proceedTeardown)
                    {
                        shouldPrune = false;
                    }
                }

                bool prunedOK = false;
                // Hoisted out of the if(shouldPrune) block so the FireWorktreeReady
                // call (now after the if(prunedOK) block) can pass the same path
                // and inspect `deferred` for the fire decision.
                string worktreePathToPrune = null;
                bool deferred = false;
                if (shouldPrune)
                {
                    // Pre-prune broadcast (task db4b18c6): tell every live
                    // terminal which worktree path is about to be removed so
                    // any agent with cwd inside it can cd out before git tries
                    // the rmdir. Without this, the agent's open handle keeps
                    // the dir alive as an empty orphan on Windows.
                    //
                    // Cycle-2 fixes:
                    //   - Subscriber awaits its HTTP deliveries with a bounded
                    //     timeout (see MainForm.OnBrokerWorktreePruning), so
                    //     this call now synchronously blocks until the agents
                    //     have been *notified* (not just enqueued). The bare
                    //     Thread.Sleep(500) "gut budget" is gone.
                    //   - WorktreePruneCoordinator marks the path as pruning
                    //     so a concurrent SpawnTerminal can refuse to launch
                    //     into a soon-to-be-deleted worktree (closes the
                    //     TOCTOU window adversary flagged).
                    worktreePathToPrune = _host.Worktrees?.GetWorktreePathForTask(taskId);

                    // Per-agent isolation: every agent worktree for the task is about
                    // to be pruned, so broadcast + mark EACH active path (not just the
                    // canonical one) so an agent cwd'd in a helper worktree can also cd
                    // out before git removes it.
                    var prunePaths = new List<string>();
                    try
                    {
                        foreach (var w in _taskDb.ListWorktreesForTask(taskId))
                        {
                            if (string.Equals(w.Status, "active", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrEmpty(w.WorktreePath)
                                && !prunePaths.Contains(w.WorktreePath))
                            {
                                prunePaths.Add(w.WorktreePath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _host.LogError($"Enumerate prune paths for {taskId} failed: {ex.Message}");
                    }
                    // Always include the canonical path (covers single-agent + the
                    // enumerate-failed fallback).
                    if (!string.IsNullOrEmpty(worktreePathToPrune) && !prunePaths.Contains(worktreePathToPrune))
                    {
                        prunePaths.Add(worktreePathToPrune);
                    }

                    var markedPaths = new List<string>();
                    foreach (var prunePath in prunePaths)
                    {
                        WorktreePruneCoordinator.MarkPruning(prunePath);
                        markedPaths.Add(prunePath);
                        var fireArgs = _host.FireWorktreePruning(taskId, prunePath, doneProjPath, task.Assignee);

                        // Cycle-3 adversary HIGH fix: if the broadcast didn't
                        // complete in time, DEFER prune — leave the worktree
                        // active so the janitor's deferred-prune pass retries
                        // once agents have likely moved on. Pruning while
                        // agents still hold cwd reduces to the partial-prune
                        // fallback + empty shell, which Pass 3 has to clean
                        // up anyway. Skipping it removes that window. If ANY
                        // path's broadcast didn't deliver, defer the whole prune.
                        if (!fireArgs.AllDelivered) deferred = true;
                    }
                    bool marked = markedPaths.Count > 0;

                    if (deferred)
                    {
                        _host.LogInfo($"Prune deferred for task {taskId} (broadcast timeout); janitor will retry.");
                        _host.RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "prune_deferred",
                            Content = $"Prune deferred for '{task.Title}' — broadcast did not complete in time; janitor will retry.",
                            RelatedId = taskId
                        });
                        // No prune attempt → don't auto-merge this pass either;
                        // janitor's retry will get there once the prune lands.
                    }
                    else
                    {
                        try
                        {
                            // Prune ALL agent worktrees for the task (canonical +
                            // helpers), not just the canonical one.
                            _host.Worktrees.PruneAllForTaskAsync(taskId, doneProjPath).GetAwaiter().GetResult();
                            prunedOK = true;
                        }
                        catch (Exception ex)
                        {
                            _host.LogError($"Worktree prune failed for task {taskId}: {ex.Message}");
                            _host.RecordActivity(new ActivityEvent
                            {
                                Terminal = task.Assignee ?? "System",
                                Type = "worktree",
                                Action = "prune_failed",
                                Content = $"Worktree prune failed for '{task.Title}'. Auto-merge skipped — see debug log for details.",
                                RelatedId = taskId
                            });
                        }
                    }

                    // Cycle-4 debugger MED fix: when deferred=true the worktree
                    // is still active on disk + git — but we've decided to
                    // prune it later via the janitor. Keep the path marked so
                    // SpawnTerminal continues to refuse it during the defer
                    // window. The janitor's TryDeferredPruneRetryAsync handles
                    // unmark-on-success (or task-reopened cancellation).
                    // For the non-deferred path: a failed prune leaves the
                    // worktree in a known-valid state (no destructive partial
                    // state outside the partial-prune fallback which already
                    // marks the DB pruned), so unmarking is safe.
                    if (marked && !deferred)
                    {
                        foreach (var markedPath in markedPaths)
                        {
                            WorktreePruneCoordinator.UnmarkPruning(markedPath);
                        }
                    }
                }

                // Phase 3 auto-merge — fires only after a successful prune.
                // Prune releases the branch lock so git can merge the task
                // branch into the main checkout's current trunk. Merge failure
                // does NOT roll back commit or prune (those are durable); the
                // dev resolves the merge manually. WorktreeReady is fired
                // inside the helper after the merge attempt completes so HUD
                // panels rebind to the post-merge state without racing the
                // merge. Cycle-4 factored this into a helper so the janitor's
                // deferred-prune retry can run the same post-prune sequence.
                if (prunedOK)
                {
                    // Delete integrated helper branches now their worktrees are gone.
                    // Their commits live in the canonical branch but not yet trunk, so
                    // force-delete (-D); the canonical branch is deleted by Phase 3.
                    foreach (var helperBranch in integratedHelperBranches)
                    {
                        try
                        {
                            _host.Merge.DeleteBranchAsync(doneProjPath, helperBranch, force: true).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            _host.LogError($"Delete helper branch '{helperBranch}' failed for {taskId}: {ex.Message}");
                        }
                    }

                    _host.PerformPostPruneMergeAndFireReady(taskId, task, doneProjPath, worktreePathToPrune);
                }
            }
            else if (status == "done"
                && MultiTerminal.Services.WorktreeConfig.IsEnabled)
            {
                // Worktree mode is ON but this task is ineligible (it resolved to no
                // worktree — no project association, ambiguous/unregistered project,
                // or a project with no filesystem path). The eligible path above runs
                // auto-commit → prune → merge; this path historically did NOTHING and
                // said nothing, so the user couldn't tell whether git automation had
                // silently failed or simply never applied. Make it explicit and
                // uniform (task 4bcd1e24, item [3]): one documented notice that the
                // task completed WITHOUT git automation and the user owns any commit.
                // (Mode-off completions are deliberately silent — there's no worktree
                // expectation to violate, so that's the global default, not a divergence.)
                _host.RecordActivity(new ActivityEvent
                {
                    Terminal = task.Assignee ?? "System",
                    Type = "worktree",
                    Action = "no_worktree_done",
                    Content = $"Task '{task.Title}' completed with no worktree ({doneSkipReason}) — no git automation ran (no auto-commit/prune/merge). Commit any changes on your current branch yourself.",
                    RelatedId = taskId
                });
            }

            // Generate changelog entry if task is marked as done and associated with a project
            if (status == "done" && !string.IsNullOrEmpty(task.ProjectId) && _host.ChangelogService != null)
            {
                try
                {
                    // Get the project to find its path
                    if (_host.TryGetProject(task.ProjectId, out var project) && !string.IsNullOrEmpty(project.Path))
                    {
                        _host.ChangelogService.AddChangelogEntry(task, project.Path);
                        _host.LogTrace($"[TaskService] Generated changelog entry for task {taskId} in project {project.Name}");
                    }
                    else
                    {
                        _host.LogTrace($"[TaskService] Skipping changelog: Project {task.ProjectId} has no path set");
                    }
                }
                catch (Exception ex)
                {
                    _host.LogTrace($"[TaskService] Failed to add changelog entry: {ex.Message}");
                }
            }

            return new UpdateTaskStatusResult { Success = true };
        }


        /// <summary>
        /// Move a task to a new position in the kanban. Handles both same-column
        /// reorder and cross-column move (status change) atomically: if newStatus
        /// differs from the current status, status updates first via the standard
        /// path (which fires resume-stack / activity / changelog side-effects),
        /// then sort_order is written. If sort_order is unchanged (newSortOrder
        /// equals current), the call is a no-op past the status update.
        ///
        /// Gap-collapse guard: when the chosen newSortOrder lands within
        /// MIN_SORT_GAP of either neighbor in the target column, the column is
        /// rebalanced into 1000-unit gaps and newSortOrder is recomputed via the
        /// post-rebalance neighbor midpoint. Prevents float collapse over many
        /// successive midpoint insertions (item 7).
        /// </summary>
        public UpdateTaskStatusResult ReorderTask(string taskId, string newStatus, double newSortOrder, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return new UpdateTaskStatusResult { Success = false, Error = $"Task not found: {taskId}" };

            if (task.IsQuickTask)
                return new UpdateTaskStatusResult { Success = false, Error = $"Cannot reorder: task {taskId} is a quick-task (immutable)." };

            // Reject NaN / ±Infinity. Either would poison the sort_order column —
            // SQL comparisons against NaN are undefined and Infinity defeats the
            // rebalance midpoint formula. Defense-in-depth: the WebView handler
            // also guards this, but a future API/MCP caller could bypass it.
            if (!double.IsFinite(newSortOrder))
                return new UpdateTaskStatusResult { Success = false, Error = $"Cannot reorder: newSortOrder must be a finite number (got {newSortOrder})." };

            // Status change first — UpdateTaskStatus handles validation, side-effects, and persistence.
            // If it fails, bail before touching sort_order. UpdateTaskStatus swaps a NEW clone into the
            // cache, so our local `task` is stale afterward — re-fetch it before the sort logic reads
            // task.Status below (otherwise we'd snapshot/rebalance the OLD column).
            if (!string.IsNullOrEmpty(newStatus) && task.Status != newStatus)
            {
                var statusResult = UpdateTaskStatus(taskId, newStatus);
                if (!statusResult.Success)
                    return statusResult;

                if (!_tasks.TryGetValue(taskId, out task))
                    return new UpdateTaskStatusResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Write-path exception (P5 / 1df2a534): sort_order is NOT persisted via the SaveTask row
            // upsert — it has a dedicated column writer (UpdateSortOrder) and a whole-column
            // RebalanceSortOrder that rewrites many sibling rows at once. The block below PERSISTS FIRST,
            // then refreshes/sets the affected in-memory tasks' SortOrder from the durable write — so the
            // cache follows the DB and cannot diverge, and a failed sort write returns Success=false with
            // the cache untouched. Routing this through MutateTask/SaveTask would be incorrect (wrong
            // writer, and it would miss the bulk rebalance). Documented bypass, like LoadPersistedTasks.
            const double MIN_SORT_GAP = 1e-6;

            // Collapse guard: snapshot neighbors in the target column AFTER the
            // status change (the task may have just moved between columns).
            var siblings = _tasks.Values
                .Where(t => t.Status == task.Status && t.Id != taskId && t.SortOrder.HasValue)
                .OrderBy(t => t.SortOrder.Value)
                .ToList();

            double? prevOrder = null, nextOrder = null;
            foreach (var sib in siblings)
            {
                if (sib.SortOrder.Value < newSortOrder) prevOrder = sib.SortOrder.Value;
                else if (sib.SortOrder.Value > newSortOrder && nextOrder == null) nextOrder = sib.SortOrder.Value;
            }

            bool tooCloseBelow = prevOrder.HasValue && (newSortOrder - prevOrder.Value) < MIN_SORT_GAP;
            bool tooCloseAbove = nextOrder.HasValue && (nextOrder.Value - newSortOrder) < MIN_SORT_GAP;

            try
            {
                if (tooCloseBelow || tooCloseAbove)
                {
                    // Rebalance the whole column. PERSIST FIRST: position the task at the desired endpoint
                    // in the DB (so RebalanceSortOrder gives it the right rank), rebalance the column in the
                    // DB, THEN reload every affected task's sort_order FROM the DB into the cache. Nothing in
                    // the cache is mutated until after the durable writes, so a throw leaves it untouched.
                    _taskDb.UpdateSortOrder(taskId, newSortOrder);
                    _taskDb.RebalanceSortOrder(task.Status);

                    foreach (var t in _tasks.Values.Where(t => t.Status == task.Status).ToList())
                    {
                        var refreshed = _taskDb.GetTask(t.Id);
                        if (refreshed != null) t.SortOrder = refreshed.SortOrder;
                    }
                }
                else
                {
                    // Persist FIRST, then update the cache — a failed sort write leaves the cache untouched.
                    _taskDb.UpdateSortOrder(taskId, newSortOrder);
                    task.SortOrder = newSortOrder;
                }
            }
            catch (Exception ex)
            {
                _host.LogError($"ReorderTask: failed to persist sort_order for {taskId}: {ex.Message}");
                return new UpdateTaskStatusResult { Success = false, Error = $"Failed to persist reorder: {ex.Message}" };
            }

            BroadcastTaskUpdate();
            return new UpdateTaskStatusResult { Success = true };
        }


        /// <summary>
        /// Update a task's title and description.
        /// </summary>
        public UpdateTaskResult UpdateTask(string taskId, string title, string description, string updatedBy = null, string plan = null, string implementationSummary = null, string testResults = null, string implementationChecklistJson = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot edit: task {taskId} is a quick-task (immutable except title). Use UpdateQuickTaskTitle for title changes." };

            if (string.IsNullOrWhiteSpace(title))
            {
                return new UpdateTaskResult { Success = false, Error = "Title cannot be empty" };
            }

            var previousTitle = task.Title;
            var assignee = task.Assignee;

            if (!TryMutateTask(taskId, t =>
            {
                t.Title = title;
                t.Description = description;

                // Update documentation fields if provided
                if (plan != null) t.Plan = plan;
                if (implementationSummary != null) t.ImplementationSummary = implementationSummary;
                if (testResults != null) t.TestResults = testResults;
                if (implementationChecklistJson != null) t.ImplementationChecklistJson = implementationChecklistJson;
            }))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist task update" };
            }

            // Record activity for the feed
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "edited",
                Content = previousTitle != title
                    ? $"Renamed task: '{previousTitle}' → '{title}'"
                    : $"Updated task: {title}",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Title-only rename for a task — the narrow-surface counterpart to
        /// <see cref="UpdateTask"/>'s multi-field edit. Works for both regular
        /// and quick-tasks (a quick-task's title is the one field its
        /// immutability contract allows to change). Used by the rename_task
        /// MCP tool / PATCH /api/tasks/{id}/title — neither needs the full
        /// edit_task surface, so this path skips description/plan/priority/
        /// project/status touches that <see cref="UpdateTask"/> would either
        /// require or clobber.
        /// </summary>
        public UpdateTaskResult RenameTask(string taskId, string newTitle, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (string.IsNullOrWhiteSpace(newTitle))
            {
                return new UpdateTaskResult { Success = false, Error = "Title cannot be empty" };
            }

            var previousTitle = task.Title;
            if (previousTitle == newTitle)
            {
                return new UpdateTaskResult { Success = true };
            }

            var isQuick = task.IsQuickTask;
            var assignee = task.Assignee;

            if (!TryMutateTask(taskId, t => t.Title = newTitle))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist rename" };
            }

            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "edited",
                Content = isQuick
                    ? $"Renamed quick task: '{previousTitle}' → '{newTitle}'"
                    : $"Renamed task: '{previousTitle}' → '{newTitle}'",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Update a task's checklist JSON.
        /// </summary>
        public UpdateTaskResult UpdateTaskChecklist(string taskId, string checklistJson)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot set checklist: task {taskId} is a quick-task (immutable; quick-tasks have no checklist)." };

            // Serialize the read-modify-write against the other checklist mutators, then broadcast
            // outside the lock. The write path (clone→persist→swap) fails closed: on a persist error the
            // cached task keeps its old checklist.
            lock (_checklistMutationLock)
            {
                try
                {
                    if (MutateTaskInternal(taskId, t =>
                    {
                        t.ChecklistJson = checklistJson ?? "[]";

                        // Auto-derive parent task status from checklist item positions
                        RecalculateAutoStatus(t);
                    }) == null)
                    {
                        return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
                    }
                }
                catch (Exception ex)
                {
                    _host.LogError($"UpdateTaskChecklist: persist failed for {taskId}: {ex.Message}");
                    return new UpdateTaskResult { Success = false, Error = "Failed to persist checklist change" };
                }
            }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Append items to a task's existing checklist without replacing it.
        /// Unlike <see cref="UpdateTaskChecklist"/> (a full replace), the caller does not have to
        /// round-trip and faithfully rebuild the whole list (which risks dropping an existing item's
        /// status/notes when re-serializing a stale snapshot). This is server-authoritative: each
        /// appended item is rebuilt from a whitelist (a non-empty "item" description plus a validated
        /// initial status); caller-supplied Done/Notes/AssignedTo/CycleCount/SortOrder are ignored so
        /// append can't forge audit history or bypass the transition state machine. The
        /// read-modify-write is serialized under <see cref="_checklistMutationLock"/> (shared with the
        /// other checklist mutators) so it won't clobber a concurrent append/transition/assign, and it
        /// fails closed if persistence throws.
        /// </summary>
        public UpdateTaskResult AppendChecklistItems(string taskId, string itemsJson)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot append checklist: task {taskId} is a quick-task (immutable; quick-tasks have no checklist)." };

            if (string.IsNullOrWhiteSpace(itemsJson))
                return new UpdateTaskResult { Success = false, Error = "No items to append (itemsJson was empty)." };

            List<ChecklistItem> rawItems;
            try
            {
                rawItems = System.Text.Json.JsonSerializer.Deserialize<List<ChecklistItem>>(itemsJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new UpdateTaskResult { Success = false, Error = $"Invalid itemsJson: {ex.Message}" };
            }

            if (rawItems == null || rawItems.Count == 0)
                return new UpdateTaskResult { Success = false, Error = "No items to append (itemsJson contained no items)." };

            // Server-side whitelist: rebuild each item from only the fields append is allowed to set.
            // A valid status is honored (defaults to "pending"); an invalid status is rejected up front
            // rather than silently corrupting the lifecycle board via RecalculateAutoStatus.
            var sanitized = new List<ChecklistItem>(rawItems.Count);
            foreach (var raw in rawItems)
            {
                if (raw == null || string.IsNullOrWhiteSpace(raw.Item))
                    return new UpdateTaskResult { Success = false, Error = "Each appended item must have a non-empty 'item' description." };

                var status = string.IsNullOrWhiteSpace(raw.Status) ? "pending" : raw.Status.Trim().ToLowerInvariant();
                if (System.Array.IndexOf(ChecklistItem.ValidStatuses, status) < 0)
                    return new UpdateTaskResult { Success = false, Error = $"Invalid status '{raw.Status}' on appended item '{raw.Item}'. Valid statuses: {string.Join(", ", ChecklistItem.ValidStatuses)}." };

                sanitized.Add(new ChecklistItem
                {
                    Item = raw.Item,
                    Status = status,
                    Done = status == "done",
                    Notes = new List<ChecklistItemNote>(),
                    AssignedTo = null,
                    CycleCount = 0
                });
            }

            // Serialize the read-modify-write against the other checklist mutators, then broadcast
            // outside the lock. The write path applies the append to a CLONE and swaps only on persist
            // success, so a DB failure leaves the cached task's checklist untouched — no manual
            // snapshot/revert needed (pre-P5 this method restored four fields by hand).
            lock (_checklistMutationLock)
            {
                try
                {
                    if (MutateTaskInternal(taskId, t =>
                    {
                        var checklist = t.GetChecklist();
                        checklist.AddRange(sanitized);
                        t.SetChecklist(checklist);

                        // Auto-derive parent task status from checklist item positions
                        RecalculateAutoStatus(t);
                    }) == null)
                    {
                        return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
                    }
                }
                catch (Exception ex)
                {
                    _host.LogError($"AppendChecklistItems: persist failed for {taskId}: {ex.Message}");
                    return new UpdateTaskResult { Success = false, Error = $"Failed to persist appended checklist items: {ex.Message}" };
                }
            }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Update a task's implementation checklist JSON.
        /// </summary>
        public UpdateTaskResult UpdateTaskImplementationChecklist(string taskId, string implementationChecklistJson)
        {
            if (!_tasks.ContainsKey(taskId))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (!TryMutateTask(taskId, t => t.ImplementationChecklistJson = implementationChecklistJson ?? "[]"))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist implementation checklist change" };
            }

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Auto-derive parent task status from checklist item positions when AutoStatus is enabled.
        /// Rules:
        /// - No checklist items → do nothing (fall through to manual status)
        /// - All items in "pending" → "todo"
        /// - Any items in "coding" or "testing" → "in_progress"
        /// - All items in "done" → "done"
        /// </summary>
        private void RecalculateAutoStatus(KanbanTask task)
        {
            if (!task.AutoStatus) return;

            var checklist = task.GetChecklist();
            if (checklist.Count == 0) return; // Zero items → keep manual status

            bool allPending = checklist.All(c => c.Status == "pending");
            bool allDone = checklist.All(c => c.Status == "done");
            bool anyActive = checklist.Any(c => c.Status == "coding" || c.Status == "testing");

            string newStatus;
            if (allDone)
                newStatus = "done";
            else if (anyActive)
                newStatus = "in_progress";
            else if (allPending)
                newStatus = "todo";
            else
                newStatus = "in_progress"; // Mix of pending + done = still in progress

            if (task.Status != newStatus)
            {
                task.Status = newStatus;
                if (newStatus == "done")
                {
                    task.SubStatus = null;
                    task.PausedAt = null;
                }
                else if (newStatus == "in_progress" && task.SubStatus == null)
                {
                    task.SubStatus = "active";
                }
            }
        }


        /// <summary>
        /// Toggle auto-status for a task. When enabled, status is derived from checklist.
        /// When disabled, user controls status directly.
        /// </summary>
        public UpdateTaskResult SetAutoStatus(string taskId, bool enabled)
        {
            if (!_tasks.ContainsKey(taskId))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (!TryMutateTask(taskId, t =>
            {
                t.AutoStatus = enabled;
                if (enabled)
                {
                    RecalculateAutoStatus(t);
                }
            }))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist auto-status change" };
            }

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Update a task's priority.
        /// </summary>
        public UpdateTaskResult UpdateTaskPriority(string taskId, string priority)
        {
            if (!_tasks.ContainsKey(taskId))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            var validPriorities = new[] { "urgent", "normal", "low" };
            if (!validPriorities.Contains(priority))
            {
                return new UpdateTaskResult { Success = false, Error = $"Invalid priority: {priority}" };
            }

            if (!TryMutateTask(taskId, t => t.Priority = priority))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist priority change" };
            }

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Update a task's project assignment.
        /// </summary>
        public UpdateTaskResult UpdateTaskProject(string taskId, string projectId)
        {
            if (!_tasks.ContainsKey(taskId))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Distinguish "user cleared the project" (empty input) from "we refused to bind because the
            // input was ambiguous" (non-empty input that NormalizeProjectId couldn't resolve). Resolve
            // the canonical id BEFORE the write path — otherwise an ambiguous short id would silently
            // overwrite a previously valid assignment with null and the call would still report Success.
            string canonical;
            if (string.IsNullOrWhiteSpace(projectId))
            {
                canonical = null;
            }
            else
            {
                canonical = _host.NormalizeProjectId(projectId);
                if (canonical == null)
                {
                    return new UpdateTaskResult
                    {
                        Success = false,
                        Error = $"Project id '{projectId}' is ambiguous (matches multiple registered projects, or is a short prefix of one). Pass the full id."
                    };
                }
            }

            // Persist-before-swap (write path): on a DB failure the cache keeps the old ProjectId — the
            // revert the pre-P5 code did by hand is now automatic (the mutation was applied to a clone
            // that is never swapped in) — and Success=false fires the caller's toast instead of the UI
            // showing "saved" over a stale row.
            if (!TryMutateTask(taskId, t => t.ProjectId = canonical))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist project change" };
            }

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Update a task's assignee (without complex stack logic - simple assignment change).
        /// </summary>
        public UpdateTaskResult UpdateTaskAssignee(string taskId, string assignee)
        {
            if (!_tasks.ContainsKey(taskId))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Normalize empty string to null.
            string normalized = string.IsNullOrEmpty(assignee) ? null : assignee;

            // Custom persist: assignee has a targeted column writer, so keep using it rather than a
            // full-row upsert. The write path still applies clone→persist→swap coherency around it.
            if (!TryMutateTask(taskId, t => t.Assignee = normalized, _ => _taskDb.UpdateTaskAssignee(taskId, normalized)))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist assignee change" };
            }

            return new UpdateTaskResult { Success = true };
        }


        // =============================================
        // Kanban Workflow: Enhanced Checklist Operations
        // =============================================

        /// <summary>
        /// Transition a checklist item to a new status with mandatory notes.
        /// Enforces the state machine: pending→coding→testing→done (with cycling).
        /// Agent can: pending→coding, coding→testing.
        /// PM/tester can: testing→coding, testing→done.
        /// </summary>
        public UpdateChecklistItemResult TransitionChecklistItem(string taskId, int itemIndex, string newStatus, string notes, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateChecklistItemResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateChecklistItemResult { Success = false, Error = $"Cannot transition checklist: task {taskId} is a quick-task (immutable; quick-tasks have no checklist)." };

            List<ChecklistItem> checklist;
            ChecklistItem item;
            string previousStatus;

            // Serialize the read-modify-write against the other checklist mutators (append, full-replace,
            // assign) so a concurrent write can't clobber this transition's status/notes/cycle update.
            lock (_checklistMutationLock)
            {
                checklist = task.GetChecklist();
                if (itemIndex < 0 || itemIndex >= checklist.Count)
                {
                    return new UpdateChecklistItemResult { Success = false, Error = $"Invalid item index: {itemIndex}. Checklist has {checklist.Count} items." };
                }

                item = checklist[itemIndex];
                previousStatus = item.Status ?? "pending";

                // Validate the transition
                var validTransitions = new Dictionary<string, string[]>
                {
                    { "pending", new[] { "coding" } },
                    { "coding", new[] { "testing" } },
                    { "testing", new[] { "coding", "done" } }
                };

                if (!validTransitions.ContainsKey(previousStatus) || !validTransitions[previousStatus].Contains(newStatus))
                {
                    return new UpdateChecklistItemResult
                    {
                        Success = false,
                        Error = $"Invalid transition: {previousStatus} → {newStatus}. Valid transitions from '{previousStatus}': {string.Join(", ", validTransitions.ContainsKey(previousStatus) ? validTransitions[previousStatus] : new[] { "none" })}"
                    };
                }

                // Notes are mandatory for coding→testing, testing→coding, testing→done
                if ((previousStatus == "coding" || previousStatus == "testing") && string.IsNullOrWhiteSpace(notes))
                {
                    return new UpdateChecklistItemResult
                    {
                        Success = false,
                        Error = $"Notes are required for {previousStatus} → {newStatus} transition. Describe what was done or what needs fixing."
                    };
                }

                // Update the item
                item.Status = newStatus;
                item.Done = newStatus == "done";

                // Track cycles (testing→coding = another cycle)
                if (previousStatus == "testing" && newStatus == "coding")
                {
                    item.CycleCount++;
                }

                // Add the transition note
                if (item.Notes == null) item.Notes = new List<ChecklistItemNote>();
                item.Notes.Add(new ChecklistItemNote
                {
                    By = updatedBy,
                    At = DateTime.UtcNow.ToString("o"),
                    Transition = $"{previousStatus} → {newStatus}",
                    Text = notes ?? ""
                });

                // Persist via the write path: apply the locally-mutated checklist to a CLONE, recalc
                // parent status, and swap into the cache only on persist success. The `checklist`/`item`
                // copies (from GetChecklist) are detached JSON copies and still drive the notifications
                // below; a DB failure leaves the cached task untouched (coherent) and returns false.
                try
                {
                    if (MutateTaskInternal(taskId, t =>
                    {
                        t.SetChecklist(checklist);
                        RecalculateAutoStatus(t);
                    }) == null)
                    {
                        return new UpdateChecklistItemResult { Success = false, Error = $"Task not found: {taskId}" };
                    }
                }
                catch (Exception ex)
                {
                    _host.LogError($"TransitionChecklistItem: persist failed for {taskId}: {ex.Message}");
                    return new UpdateChecklistItemResult { Success = false, Error = "Failed to persist checklist transition" };
                }
            }

            BroadcastTaskUpdate();

            // Record activity
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? "System",
                Type = "task",
                Action = "checklist_transition",
                Content = $"Checklist item '{item.Item}': {previousStatus} → {newStatus}",
                RelatedId = taskId
            });

            bool escalation = item.CycleCount >= 4;

            // Auto-generate inbox notifications
            try
            {
                // coding → testing: Notify PM that item is ready for testing
                if (previousStatus == "coding" && newStatus == "testing")
                {
                    _host.CreateInboxNotification(
                        _host.DefaultInboxRecipient,
                        taskId,
                        task.Title,
                        itemIndex,
                        item.Item,
                        "ready_for_testing",
                        $"{updatedBy} finished '{item.Item}' - ready for your testing. {notes}",
                        updatedBy);
                }

                // Escalation: 4+ coding↔testing cycles
                if (escalation && previousStatus == "testing" && newStatus == "coding")
                {
                    _host.CreateInboxNotification(
                        _host.DefaultInboxRecipient,
                        taskId,
                        task.Title,
                        itemIndex,
                        item.Item,
                        "escalation",
                        $"Item '{item.Item}' has cycled {item.CycleCount} times between coding and testing - may need discussion.",
                        updatedBy);
                }

                // Task complete: Check if ALL items are now done
                if (newStatus == "done")
                {
                    var allDone = checklist.All(i => i.Status == "done");
                    if (allDone)
                    {
                        _host.CreateInboxNotification(
                            _host.DefaultInboxRecipient,
                            taskId,
                            task.Title,
                            null,
                            null,
                            "task_complete",
                            $"All {checklist.Count} items on '{task.Title}' are done - task ready for final sign-off.",
                            updatedBy);
                    }
                }
            }
            catch (Exception ex)
            {
                _host.LogError($"Failed to create inbox notification: {ex.Message}");
            }

            return new UpdateChecklistItemResult
            {
                Success = true,
                ItemName = item.Item,
                PreviousStatus = previousStatus,
                NewStatus = newStatus,
                CycleCount = item.CycleCount,
                EscalationTriggered = escalation
            };
        }


        /// <summary>
        /// Assign a checklist item to a specific agent (or unassign by passing null).
        /// </summary>
        public UpdateTaskResult AssignChecklistItem(string taskId, int itemIndex, string assignee)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            lock (_checklistMutationLock)
            {
                var checklist = task.GetChecklist();
                if (itemIndex < 0 || itemIndex >= checklist.Count)
                {
                    return new UpdateTaskResult { Success = false, Error = $"Invalid item index: {itemIndex}. Checklist has {checklist.Count} items." };
                }

                try
                {
                    if (MutateTaskInternal(taskId, t =>
                    {
                        var list = t.GetChecklist();
                        list[itemIndex].AssignedTo = assignee;
                        t.SetChecklist(list);
                    }) == null)
                    {
                        return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
                    }
                }
                catch (Exception ex)
                {
                    _host.LogError($"AssignChecklistItem: persist failed for {taskId}: {ex.Message}");
                    return new UpdateTaskResult { Success = false, Error = "Failed to persist checklist assignment" };
                }
            }

            BroadcastTaskUpdate();

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Update a task's continuation notes for session handoff.
        /// </summary>
        public UpdateContinuationResult UpdateTaskContinuation(string taskId, string continuationNotes, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateContinuationResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            var assignee = task.Assignee;
            var title = task.Title;

            if (!TryMutateTask(taskId, t => t.ContinuationNotes = continuationNotes))
            {
                return new UpdateContinuationResult { Success = false, Error = "Failed to persist continuation notes" };
            }

            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "continuation_updated",
                Content = $"Updated continuation notes for: {title}",
                RelatedId = taskId
            });

            return new UpdateContinuationResult { Success = true };
        }


        /// <summary>
        /// Update a task's plan field.
        /// </summary>
        public UpdateTaskResult UpdateTaskPlan(string taskId, string plan, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot set plan: task {taskId} is a quick-task (immutable; quick-tasks have no plan)." };

            var assignee = task.Assignee;
            var title = task.Title;

            if (!TryMutateTask(taskId, t => t.Plan = plan))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist plan change" };
            }

            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "plan_updated",
                Content = $"Updated plan for: {title}",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Update a task's implementation summary and/or test results.
        /// </summary>
        public UpdateTaskResult UpdateTaskSummaryFields(string taskId, string implementationSummary, string testResults, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new UpdateTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.IsQuickTask)
                return new UpdateTaskResult { Success = false, Error = $"Cannot set summary/test results: task {taskId} is a quick-task (immutable)." };

            var assignee = task.Assignee;
            var title = task.Title;

            if (!TryMutateTask(taskId, t =>
            {
                if (implementationSummary != null) t.ImplementationSummary = implementationSummary;
                if (testResults != null) t.TestResults = testResults;
            }))
            {
                return new UpdateTaskResult { Success = false, Error = "Failed to persist summary change" };
            }

            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "summary_updated",
                Content = $"Updated summary/results for: {title}",
                RelatedId = taskId
            });

            return new UpdateTaskResult { Success = true };
        }


        /// <summary>
        /// Set a task as active, auto-pausing all other active tasks for the same assignee.
        /// Enforces the "only one active task" rule.
        /// </summary>
        public SetTaskActiveResult SetTaskActive(string taskId, string updatedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new SetTaskActiveResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            if (task.Status != "in_progress")
            {
                return new SetTaskActiveResult { Success = false, Error = $"Task must be in_progress to set active. Current status: {task.Status}" };
            }

            var assignee = task.Assignee ?? updatedBy;

            // Per-agent worktree isolation (task bab81a92): the agent performing the
            // activation owns its own worktree. The assignee holds the canonical
            // worktree (task/<id>); any other activator is a helper on a
            // task/<id>--<slug> branch. Auto-pause + activity tracking below stay
            // keyed on the task assignee (unchanged single-active-task model).
            string actingAgent = updatedBy ?? assignee;
            bool isActingAssignee = string.IsNullOrEmpty(task.Assignee)
                || string.Equals(actingAgent, task.Assignee, StringComparison.OrdinalIgnoreCase);

            // Activate THIS task FIRST, and make its persist FATAL (P5 pipeline A + Alice's SetTaskActive
            // ruling): if the activation doesn't persist we return Success=false BEFORE touching any
            // sibling, so a failed activation leaves the siblings unpaused and the board coherent. Reassign
            // `task` to the swapped-in cache entry so the worktree / project-id self-heal below operates on
            // the live cached object.
            try
            {
                var activated = MutateTaskInternal(taskId, t =>
                {
                    t.SubStatus = "active";
                    t.PausedAt = null;
                });
                if (activated == null)
                {
                    return new SetTaskActiveResult { Success = false, Error = $"Task not found: {taskId}" };
                }

                task = activated;
            }
            catch (Exception ex)
            {
                _host.LogError($"SetTaskActive: failed to set task active: {ex.Message}");
                return new SetTaskActiveResult { Success = false, Error = $"Failed to persist activation: {ex.Message}" };
            }

            var pausedIds = new List<string>();
            var pausedTitles = new List<string>();

            // Capture the previously-active task's id + worktree (for the TaskActiveChanged event below),
            // then pause the OTHER active tasks for this assignee — BEST-EFFORT (the activation, the durable
            // core, already succeeded; a sibling-pause miss is logged, not fatal). One assignee has at most
            // one active task, so the first match wins. There is a synchronous in-cache window where this
            // task and a sibling are both "active", but no event is broadcast until the end so subscribers
            // never observe it.
            string oldTaskId = null;
            string oldWorktreePath = null;

            foreach (var kvp in _tasks)
            {
                var other = kvp.Value;
                if (other.Id != taskId &&
                    other.Status == "in_progress" &&
                    other.SubStatus == "active" &&
                    string.Equals(other.Assignee, assignee, StringComparison.OrdinalIgnoreCase))
                {
                    if (oldTaskId == null)
                    {
                        oldTaskId = other.Id;
                        oldWorktreePath = _host.Worktrees?.GetWorktreePathForTask(other.Id, actingAgent)
                                          ?? _host.Worktrees?.GetWorktreePathForTask(other.Id);
                    }

                    pausedIds.Add(other.Id);
                    pausedTitles.Add(other.Title);

                    var otherId = other.Id;
                    try
                    {
                        MutateTaskInternal(otherId, t =>
                        {
                            t.SubStatus = "paused";
                            t.PausedAt = DateTime.UtcNow;
                        });
                    }
                    catch (Exception ex) { _host.LogError($"SetTaskActive: failed to pause task {otherId}: {ex.Message}"); }
                }
            }

            // Auto-helper: an agent that activates a task it doesn't own (not the
            // assignee, not already a helper) is registered as a helper so the
            // helper list stays consistent with the per-agent worktree it's about
            // to receive. Emits the standard helper-added notification.
            if (!isActingAssignee
                && !string.IsNullOrEmpty(actingAgent)
                && !task.Helpers.Contains(actingAgent, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    AddHelper(taskId, actingAgent, actingAgent).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _host.LogError($"Auto-add helper '{actingAgent}' on task {taskId} failed: {ex.Message}");
                }
            }

            // Phase 1 worktree isolation — gated by MULTITERMINAL_WORKTREE_MODE.
            // Materialize a fresh worktree on `git worktree add` so subsequent agent
            // spawns can be rooted there. Idempotent: returns the existing record
            // when one already exists. Failure is non-fatal — the task still goes
            // active so the user is not blocked by git issues.
            //
            // Eligibility (mode-on + resolvable project path) is decided by the
            // shared TryResolveWorktreeEligibility predicate so activation and the
            // done-gate can never disagree (task 4bcd1e24). The predicate also
            // hands back the canonical project id so we can opportunistically
            // self-heal a legacy truncated 8-char ProjectId on the row — future
            // lookups then succeed without repeating the prefix scan.
            if (_host.TryResolveWorktreeEligibility(task, out string activeProjPath, out string canonicalProjectId, out string worktreeSkipReason))
            {
                bool canCreateWorktree = true;

                if (!string.Equals(canonicalProjectId, task.ProjectId, StringComparison.Ordinal))
                {
                    // Self-heal the legacy ProjectId THROUGH the write path. This must clone the CURRENT
                    // cache entry and swap coherently: the AddHelper call above may have swapped a fresh
                    // clone into _tasks[taskId] (helper added), so the local `task` is now stale — mutating
                    // it in place + SaveTask would persist the canonical id to the DB while the cache kept
                    // the OLD one (the exact cache/DB divergence P5 eliminates). MutateTaskInternal persists
                    // FIRST, so a failure leaves cache+DB coherent on the old id with no manual revert; on
                    // success `task` is refreshed to the coherent swapped-in entry for the block below.
                    try
                    {
                        var healed = MutateTaskInternal(taskId, t => t.ProjectId = canonicalProjectId);
                        if (healed != null)
                        {
                            task = healed;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Persistence failed — the cache is left on the old id (coherent with the DB row),
                        // and we skip worktree creation under a binding the task row doesn't claim.
                        canCreateWorktree = false;
                        _host.LogError($"Failed to persist normalized ProjectId for task {taskId}: {ex.Message}");
                        _host.RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "create_skipped",
                            Content = $"Worktree creation skipped for '{task.Title}': could not persist canonical project id — see debug log for details.",
                            RelatedId = taskId
                        });
                    }
                }

                if (canCreateWorktree)
                {
                    try
                    {
                        // Serialize against task-done helper integration so a helper
                        // worktree can't be created while the canonical branch is mid-merge.
                        lock (_host.TaskWorktreeLock(taskId))
                        {
                            // Re-check under the lock: if the task went done (teardown
                            // started) since we entered SetTaskActive, don't create a
                            // worktree the teardown would never see / prune.
                            if (string.Equals(task.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
                            {
                                _host.Worktrees.CreateForTaskAsync(taskId, actingAgent, isActingAssignee, activeProjPath).GetAwaiter().GetResult();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _host.LogError($"Worktree create failed for task {taskId}: {ex.Message}");
                        _host.RecordActivity(new ActivityEvent
                        {
                            Terminal = task.Assignee ?? "System",
                            Type = "worktree",
                            Action = "create_failed",
                            Content = $"Worktree creation failed for '{task.Title}' — see debug log for details.",
                            RelatedId = taskId
                        });
                    }
                }
            }
            else if (MultiTerminal.Services.WorktreeConfig.IsEnabled
                && !string.IsNullOrEmpty(task.ProjectId))
            {
                // Worktree mode is on AND the task claims a project, but the
                // project couldn't be resolved. The predicate already classified
                // the cause (ambiguous input, missing registration, or
                // registered-without-path) — surface it so the user can act on
                // the right one without reading debug logs.
                _host.RecordActivity(new ActivityEvent
                {
                    Terminal = task.Assignee ?? "System",
                    Type = "worktree",
                    Action = "create_skipped",
                    Content = $"Worktree creation skipped for '{task.Title}': project '{task.ProjectId}' could not be resolved ({worktreeSkipReason}).",
                    RelatedId = taskId
                });
            }

            BroadcastTaskUpdate();

            // Update _host.ActivityService so the Task HUD can find the active task for this terminal
            _host.ActivityService?.UpdateActivity(assignee, "working", $"Task: {task.Title}", taskId);

            _host.RecordActivity(new ActivityEvent
            {
                Terminal = updatedBy ?? assignee ?? "System",
                Type = "task",
                Action = "set_active",
                Content = pausedIds.Count > 0
                    ? $"Set '{task.Title}' active. Auto-paused {pausedIds.Count} task(s): {string.Join(", ", pausedTitles)}"
                    : $"Set '{task.Title}' active.",
                RelatedId = taskId
            });

            // AC2: notify subscribers (MainForm pushes the event over the
            // agent's Claude Code Channel so the agent-side auto-cd hook can
            // react). Resolve newWorktreePath fresh from the worktree manager
            // so we reflect whatever the create attempt above produced (null
            // when worktree mode is off, project unresolvable, or git failed).
            // Resolve the ACTING agent's own worktree (canonical for the assignee,
            // helper for a helper) so the event — and the agent-side auto-cd hook —
            // target the worktree that agent will actually work in.
            string newWorktreePath = _host.Worktrees?.GetWorktreePathForTask(taskId, actingAgent)
                                     ?? _host.Worktrees?.GetWorktreePathForTask(taskId);
            try
            {
                _host.RaiseTaskActiveChanged(new TaskActiveChangedEventArgs(
                    agentName: actingAgent,
                    oldTaskId: oldTaskId,
                    oldWorktreePath: oldWorktreePath,
                    newTaskId: taskId,
                    newWorktreePath: newWorktreePath));
            }
            catch (Exception ex)
            {
                _host.LogError($"TaskActiveChanged subscribers threw: {ex.Message}");
            }

            return new SetTaskActiveResult
            {
                Success = true,
                PausedTaskIds = pausedIds,
                PausedTaskTitles = pausedTitles
            };
        }


        /// <summary>
        /// Delete a task from the Kanban board.
        /// </summary>
        public DeleteTaskResult DeleteTask(string taskId, string deletedBy = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new DeleteTaskResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Write path: delete from the DB FIRST, then remove from the cache — the mirror of the
            // insert/mutate ordering. Pre-P5 this removed from the cache first and swallowed a DB-delete
            // failure, so a failed delete left the task gone from the UI but resurrected on next restart.
            try
            {
                if (!DeleteTaskInternal(taskId))
                {
                    return new DeleteTaskResult { Success = false, Error = $"Task not found: {taskId}" };
                }
            }
            catch (Exception ex)
            {
                _host.LogError($"DeleteTask: failed to delete task {taskId}: {ex.Message}");
                return new DeleteTaskResult { Success = false, Error = $"Failed to delete task: {ex.Message}" };
            }

            // Clean up any attachments for this task
            _host.CleanupTaskAttachments(taskId);

            BroadcastTaskUpdate();

            // Record activity for the feed
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = deletedBy ?? task.Assignee ?? "System",
                Type = "task",
                Action = "deleted",
                Content = $"Deleted task: {task.Title}",
                RelatedId = taskId
            });

            return new DeleteTaskResult { Success = true };
        }


        // =============================================
        // Task Relationships
        // =============================================

        public AddRelationshipResult AddRelationship(string sourceTaskId, string targetTaskId, string type, string createdBy)
        {
            // Validate type
            var validTypes = new[] { "blocks", "depends_on", "related_to" };
            if (!validTypes.Contains(type))
                return new AddRelationshipResult { Success = false, Error = $"Invalid relationship type: {type}. Must be: blocks, depends_on, related_to" };

            // Validate both tasks exist
            if (!_tasks.ContainsKey(sourceTaskId))
                return new AddRelationshipResult { Success = false, Error = $"Source task not found: {sourceTaskId}" };
            if (!_tasks.ContainsKey(targetTaskId))
                return new AddRelationshipResult { Success = false, Error = $"Target task not found: {targetTaskId}" };

            // Prevent self-reference
            if (sourceTaskId == targetTaskId)
                return new AddRelationshipResult { Success = false, Error = "Cannot create relationship to self" };

            try
            {
                // Add the forward relationship
                var forwardId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _taskDb.AddRelationship(forwardId, sourceTaskId, targetTaskId, type, createdBy);

                // Auto-create inverse
                var inverseType = type switch
                {
                    "blocks" => "depends_on",
                    "depends_on" => "blocks",
                    "related_to" => "related_to",
                    _ => type
                };
                var inverseId = Guid.NewGuid().ToString("N").Substring(0, 8);
                _taskDb.AddRelationship(inverseId, targetTaskId, sourceTaskId, inverseType, createdBy);

                return new AddRelationshipResult { Success = true };
            }
            catch (Exception ex)
            {
                _host.LogError($"AddRelationship failed: {ex.Message}");
                return new AddRelationshipResult { Success = false, Error = ex.Message };
            }
        }


        public RemoveRelationshipResult RemoveRelationship(string sourceTaskId, string targetTaskId)
        {
            if (!_tasks.ContainsKey(sourceTaskId))
                return new RemoveRelationshipResult { Success = false, Error = $"Source task not found: {sourceTaskId}" };

            try
            {
                _taskDb.RemoveRelationshipsBetween(sourceTaskId, targetTaskId);
                return new RemoveRelationshipResult { Success = true };
            }
            catch (Exception ex)
            {
                _host.LogError($"RemoveRelationship failed: {ex.Message}");
                return new RemoveRelationshipResult { Success = false, Error = ex.Message };
            }
        }


        public GetRelationshipsResult GetRelationships(string taskId)
        {
            if (!_tasks.ContainsKey(taskId))
                return new GetRelationshipsResult { Success = false, Error = $"Task not found: {taskId}" };

            try
            {
                // Get relationships where this task is the source (our perspective)
                var allRels = _taskDb.GetRelationshipsForTask(taskId);
                var ourRels = allRels.Where(r => r.SourceTaskId == taskId).ToList();
                return new GetRelationshipsResult { Success = true, Relationships = ourRels };
            }
            catch (Exception ex)
            {
                _host.LogError($"GetRelationships failed: {ex.Message}");
                return new GetRelationshipsResult { Success = false, Error = ex.Message };
            }
        }


        // =============================================
        // Task File Links
        // =============================================

        public LinkFileResult LinkFile(string taskId, string filePath, string description, int? lineStart, int? lineEnd, string addedBy, int? checklistItemIndex = null)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                return new LinkFileResult { Success = false, Error = $"Task not found: {taskId}" };

            if (string.IsNullOrWhiteSpace(filePath))
                return new LinkFileResult { Success = false, Error = "File path is required" };

            // checklistItemIndex must be a real 0-based index into the live checklist.
            // Negative values or out-of-range positives produce phantom rows that survive
            // forever and silently break per-item routing (Run 2 debugger HIGH / LOW).
            // Note: this gate is best-effort — a concurrent checklist edit between this
            // read and the link insert could still produce a phantom row. ComputeItemsToBounce
            // (TasksPanelControl) filters phantom indexes at routing time as the actual
            // tolerance layer (adversary Run 3 MEDIUM acknowledged).
            if (checklistItemIndex.HasValue)
            {
                if (checklistItemIndex.Value < 0)
                {
                    return new LinkFileResult { Success = false, Error = $"checklistItemIndex must be >= 0 (got {checklistItemIndex.Value}); omit for task-scoped links" };
                }
                int itemCount = 0;
                try
                {
                    if (!string.IsNullOrEmpty(task.ChecklistJson))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(task.ChecklistJson);
                        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            itemCount = doc.RootElement.GetArrayLength();
                        }
                    }
                }
                catch
                {
                    // If we can't parse the checklist, fall through and let the row be
                    // accepted — better than blocking a link on transient parse error.
                    itemCount = -1;
                }
                if (itemCount == 0)
                {
                    return new LinkFileResult { Success = false, Error = "task has no checklist items; omit checklistItemIndex for task-scoped links" };
                }
                if (itemCount > 0 && checklistItemIndex.Value >= itemCount)
                {
                    return new LinkFileResult { Success = false, Error = $"checklistItemIndex {checklistItemIndex.Value} out of range; task has {itemCount} item(s)" };
                }
            }

            try
            {
                var id = Guid.NewGuid().ToString("N").Substring(0, 8);
                _taskDb.AddFileLink(id, taskId, filePath, description, lineStart, lineEnd, addedBy, checklistItemIndex);
                var files = _taskDb.GetFileLinksForTask(taskId);
                return new LinkFileResult { Success = true, FileCount = files.Count };
            }
            catch (Exception ex)
            {
                _host.LogError($"LinkFile failed: {ex.Message}");
                return new LinkFileResult { Success = false, Error = ex.Message };
            }
        }


        public UnlinkFileResult UnlinkFile(string taskId, string filePath)
        {
            if (!_tasks.ContainsKey(taskId))
                return new UnlinkFileResult { Success = false, Error = $"Task not found: {taskId}" };

            try
            {
                _taskDb.RemoveFileLink(taskId, filePath);
                return new UnlinkFileResult { Success = true };
            }
            catch (Exception ex)
            {
                _host.LogError($"UnlinkFile failed: {ex.Message}");
                return new UnlinkFileResult { Success = false, Error = ex.Message };
            }
        }


        public GetTaskFilesResult GetTaskFiles(string taskId)
        {
            if (!_tasks.ContainsKey(taskId))
                return new GetTaskFilesResult { Success = false, Error = $"Task not found: {taskId}" };

            try
            {
                var files = _taskDb.GetFileLinksForTask(taskId);
                return new GetTaskFilesResult { Success = true, Files = files };
            }
            catch (Exception ex)
            {
                _host.LogError($"GetTaskFiles failed: {ex.Message}");
                return new GetTaskFilesResult { Success = false, Error = ex.Message };
            }
        }


        // Returns:
        //   non-null set of item indexes when item-scoped links exist for the file.
        //   null when ANY task-scoped link (checklist_item_index IS NULL) exists for
        //     the file — caller treats as "applies to all items" (backward compat).
        //   empty set when no link exists at all — caller decides fallback.
        // On exception: returns null (= same fallback path as task-scoped). Returning
        // empty would have masked a transient DB hiccup as "no link" and silently
        // dropped that comment from routing while other comments still computed a
        // routed set; null instead routes to bulk-bounce so notes never go missing
        // because of an intermittent SQLite error (Run 2 code-review/debugger LOW).
        public HashSet<int> GetItemsLinkedToFile(string taskId, string filePath)
        {
            try
            {
                return _taskDb.GetItemsLinkedToFile(taskId, filePath);
            }
            catch (Exception ex)
            {
                _host.LogError($"GetItemsLinkedToFile failed: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// Get all tasks.
        /// </summary>
        public List<KanbanTask> GetTasks()
        {
            return _tasks.Values.OrderBy(t => t.CreatedAt).ToList();
        }


        /// <summary>
        /// Get all tasks filtered by project ID.
        /// </summary>
        /// <param name="projectId">Project ID to filter by, or null for all tasks.</param>
        public List<KanbanTask> GetTasks(string projectId)
        {
            if (string.IsNullOrEmpty(projectId))
                return GetTasks();

            return _tasks.Values
                .Where(t => t.ProjectId == projectId)
                .OrderBy(t => t.CreatedAt)
                .ToList();
        }


        /// <summary>
        /// Get the active in-progress task for a specific agent.
        /// Checks in-memory cache first, falls back to database.
        /// </summary>
        public KanbanTask GetMyActiveTask(string agentName)
        {
            if (string.IsNullOrEmpty(agentName))
                return null;

            // Check in-memory cache first. If a sibling-pause failed during SetTaskActive/ClaimTask, an
            // agent can transiently have >1 active task (accepted limitation — the atomic pause+activate
            // fix is follow-up 7c59c004). Make the resolution DETERMINISTIC and OBSERVABLE rather than
            // silently nondeterministic (P5 pipeline Run 2 mitigation): log loudly and pick a stable winner.
            // The DB fallback below orders by updated_at DESC (newest activation); the cache has no
            // activation timestamp, so it uses a stable CreatedAt-desc / id tiebreak.
            var actives = _tasks.Values
                .Where(t =>
                    t.Assignee != null &&
                    t.Assignee.Equals(agentName, StringComparison.OrdinalIgnoreCase) &&
                    t.Status == "in_progress" &&
                    t.SubStatus == "active")
                .OrderByDescending(t => t.CreatedAt)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToList();

            if (actives.Count > 1)
            {
                _host.LogError($"GetMyActiveTask: {actives.Count} active tasks for '{agentName}' — single-active invariant violated (a sibling-pause likely failed; see follow-up 7c59c004). Resolving deterministically to '{actives[0].Id}'.");
            }

            return actives.FirstOrDefault() ?? _taskDb.GetActiveTaskForAgent(agentName);
        }


        /// <summary>
        /// Resolve the agent's active task for WORKTREE/CWD purposes. Distinct
        /// from <see cref="GetMyActiveTask"/>, which is strict (assignee +
        /// <c>sub_status='active'</c>) and must stay that way so kanban callers
        /// (session-start, get_my_active_task) never see a paused task.
        ///
        /// <para>A HELPER is never the task assignee, so the strict lookup always
        /// misses for them — yet a helper still owns a per-agent worktree it must
        /// be routed into (get_active_worktree, and the spawn-cwd resolvers
        /// <see cref="ResolveTaskWorktreePath"/> / <see cref="ResolveTaskRepoRoot"/>).
        /// The helper's own active <c>task_worktrees</c> row is the per-agent
        /// active pointer; resolve the task from it. Falls back to the strict
        /// result for the assignee (unchanged), so assignee resolution is
        /// byte-identical. Most-recent active worktree row wins when an agent
        /// helps on several tasks (task bab81a92, fixes acceptance scenario 2b).</para>
        /// </summary>
        public KanbanTask ResolveActiveTaskForAgent(string agentName)
        {
            if (string.IsNullOrEmpty(agentName)) return null;

            // Assignee path — strict, unchanged.
            var strict = GetMyActiveTask(agentName);
            if (strict != null) return strict;

            // Helper path — resolve via the agent's own active worktree row.
            try
            {
                var wt = _taskDb?.GetActiveWorktreeForAgent(agentName);
                if (wt != null && !string.IsNullOrEmpty(wt.TaskId))
                {
                    return _tasks.TryGetValue(wt.TaskId, out var helperTask)
                        ? helperTask
                        : _taskDb.GetTask(wt.TaskId);
                }
            }
            catch (Exception ex)
            {
                _host.LogError($"'{agentName}' threw: {ex.Message} — no helper worktree resolved");
            }

            return null;
        }


        /// <summary>
        /// Broadcast task update to all listeners. Public so the broker's task-attachment region (which
        /// stays broker-side) can refresh the task list after an attachment add/remove (ticket e7e89f4b).
        /// </summary>
        public void BroadcastTaskUpdate()
        {
            var tasks = GetTasks();
            _host.RaiseTasksUpdated(tasks);
        }


        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Single task write path (P5 / ticket 1df2a534).
        //
        // ORDERING RULE — every task mutation goes: (1) clone the cached task, (2) apply the change to
        // the CLONE, (3) persist the clone via _taskDb, (4) ONLY on persist success swap the clone into
        // the _tasks cache, (5) raise the change event (via RaiseSafe/BroadcastTaskUpdate). Persist-
        // BEFORE-swap is the load-bearing invariant: if the DB write throws, the cache is left untouched,
        // so cache and DB never diverge on the error path — the exact bug this ticket fixes (21 of the
        // pre-P5 mutators edited the CACHED reference in place and then persisted, so any persist failure
        // left the cache ahead of the DB). Cloning also means a concurrent reader sees either the old or
        // the new task WHOLE, never a half-applied mutation. Do NOT invert steps 3 and 4.
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Core of the single task write path: clone → mutate the clone → persist → swap into cache.
        /// Returns the swapped (new) task, or null if <paramref name="taskId"/> is not cached. Does NOT
        /// broadcast — the caller decides (single-task mutators call <see cref="MutateTask"/>; multi-task
        /// methods swap several tasks then broadcast once). A persist exception propagates and leaves the
        /// cache untouched (coherent-fail).
        /// </summary>
        private KanbanTask MutateTaskInternal(string taskId, Action<KanbanTask> mutate, Action<KanbanTask> persist = null)
        {
            if (!_tasks.TryGetValue(taskId, out var current))
            {
                return null;
            }

            var updated = current.Clone();
            mutate(updated);

            // persist FIRST — if this throws, the cache stays coherent. Defaults to a full-row SaveTask;
            // pass a custom action for a side-table / targeted-column write (e.g. RespondToStale's
            // ClearStaleTracking / RecordStaleResponse) where the clone mirrors what that writer persists.
            if (persist != null)
            {
                persist(updated);
            }
            else
            {
                _taskDb.SaveTask(updated);
            }

            _tasks[taskId] = updated;   // swap into cache only after the DB write succeeded
            return updated;
        }


        /// <summary>
        /// The single-task write path for the common "look up task, change fields, persist, broadcast"
        /// pattern. Clones the cached task, applies <paramref name="mutate"/> to the clone, persists it,
        /// and ONLY on persist success swaps the clone into the cache and broadcasts.
        /// <para>Returns <c>true</c> if the change persisted and is now live; <c>false</c> if the task
        /// wasn't cached OR the DB write threw — in the failure case the cache is left untouched (coherent,
        /// no divergence) and the exception is logged, NOT propagated. This generalizes the careful
        /// revert-on-failure pattern that <see cref="UpdateTaskProject"/> already used by hand; callers
        /// map <c>false</c> to their <c>Success=false</c> result (pre-check <see cref="GetTask"/> first if
        /// they need to distinguish not-found from persist-failure).</para>
        /// <para><paramref name="persist"/> defaults to a full-row <c>_taskDb.SaveTask</c>; pass a custom
        /// action for mutations that persist to a side table (e.g. helpers in <c>task_helpers</c>) where a
        /// row upsert wouldn't capture the change.</para>
        /// </summary>
        private bool TryMutateTask(string taskId, Action<KanbanTask> mutate, Action<KanbanTask> persist = null)
        {
            if (!_tasks.TryGetValue(taskId, out var current))
            {
                return false;
            }

            var updated = current.Clone();
            mutate(updated);
            try
            {
                if (persist != null)
                {
                    persist(updated);
                }
                else
                {
                    _taskDb.SaveTask(updated);
                }
            }
            catch (Exception ex)
            {
                // Persist failed — leave the cache on the OLD copy (coherent with the DB row) and report
                // failure. Pre-P5 these sites swallowed the error and still returned Success=true.
                _host.LogError($"TryMutateTask: persist failed for task {taskId}: {ex.Message}");
                return false;
            }

            _tasks[taskId] = updated;   // swap into cache only after the DB write succeeded
            BroadcastTaskUpdate();
            return true;
        }


        /// <summary>
        /// Insert a NEW task through the write path: persist → add to cache (persist-before-add, same
        /// coherency rule as <see cref="MutateTaskInternal"/>). Does NOT broadcast; the caller decides
        /// when. Returns the inserted task for chaining.
        /// </summary>
        private KanbanTask InsertTaskInternal(KanbanTask task)
        {
            _taskDb.SaveTask(task);   // persist FIRST
            _tasks[task.Id] = task;   // add to cache only after the DB write succeeded
            return task;
        }


        /// <summary>
        /// Delete a task through the write path: delete from DB → remove from cache (DB-before-cache,
        /// the mirror of the insert/mutate ordering). Does NOT broadcast. Returns false if the task was
        /// not cached.
        /// </summary>
        private bool DeleteTaskInternal(string taskId)
        {
            if (!_tasks.ContainsKey(taskId))
            {
                return false;
            }

            _taskDb.DeleteTask(taskId);       // delete from DB FIRST
            _tasks.TryRemove(taskId, out _);  // remove from cache only after the DB delete succeeded
            return true;
        }


        /// <summary>
        /// Debug-only cache-coherency check (P5 / 1df2a534): samples up to <paramref name="sampleSize"/>
        /// cached tasks and compares the persisted tasks-ROW fields against the DB row, reporting any
        /// divergence. (<see cref="KanbanTask.Helpers"/> live in the <c>task_helpers</c> side table, not
        /// the tasks row, so they are out of scope here.) Under the single write path the answer is always
        /// "coherent" — this is the observable evidence that clone→persist→swap keeps <c>_tasks</c> in
        /// lockstep with the tasks table. Not on any hot path; exposed via the debug endpoint and exercised
        /// as verification in the P5 test suite.
        /// </summary>
        public CacheCoherencyReport VerifyCacheCoherency(int sampleSize = 50)
        {
            var report = new CacheCoherencyReport();
            var cached = _tasks.Values.ToList();
            report.CachedCount = cached.Count;

            // Take up to sampleSize cached tasks (sampleSize <= 0 checks EVERY cached task). Order is
            // ConcurrentDictionary enumeration order — an arbitrary but sufficient sample for a coherency
            // spot-check; callers wanting full coverage (e.g. the P5 test) pass sampleSize = 0.
            IEnumerable<KanbanTask> sample = sampleSize > 0 && cached.Count > sampleSize
                ? cached.Take(sampleSize)
                : cached;

            foreach (var cachedTask in sample)
            {
                report.Checked++;
                var dbTask = _taskDb.GetTask(cachedTask.Id);
                if (dbTask == null)
                {
                    report.Divergences.Add($"{cachedTask.Id}: present in cache, missing from DB");
                    continue;
                }

                // Compare the persisted tasks-row STRING/NUMERIC/BOOL fields (not a token subset) so
                // "coherent" is meaningful — a divergence on any of these is caught. DateTime columns
                // (CreatedAt, PausedAt, FlaggedStaleAt, StaleNotifiedAt) are DELIBERATELY excluded: SQLite
                // storage truncates sub-tick precision, so an exact-equality check would false-positive on
                // a faithful round-trip. They are still written by SaveTask like every other field, so the
                // clone→persist→swap guarantee covers them; they're just not exact-comparable spot-checks.
                if (cachedTask.Status != dbTask.Status
                    || cachedTask.SubStatus != dbTask.SubStatus
                    || cachedTask.Assignee != dbTask.Assignee
                    || cachedTask.Title != dbTask.Title
                    || cachedTask.Description != dbTask.Description
                    || cachedTask.Priority != dbTask.Priority
                    || cachedTask.ProjectId != dbTask.ProjectId
                    || cachedTask.Plan != dbTask.Plan
                    || cachedTask.ChecklistJson != dbTask.ChecklistJson
                    || cachedTask.ImplementationChecklistJson != dbTask.ImplementationChecklistJson
                    || cachedTask.ImplementationSummary != dbTask.ImplementationSummary
                    || cachedTask.TestResults != dbTask.TestResults
                    || cachedTask.ReviewNotes != dbTask.ReviewNotes
                    || cachedTask.ContinuationNotes != dbTask.ContinuationNotes
                    || cachedTask.SortOrder != dbTask.SortOrder
                    || cachedTask.AutoStatus != dbTask.AutoStatus
                    || cachedTask.IsQuickTask != dbTask.IsQuickTask
                    || cachedTask.CreatedBy != dbTask.CreatedBy
                    || cachedTask.StaleLevel != dbTask.StaleLevel
                    || cachedTask.StaleResponse != dbTask.StaleResponse)
                {
                    report.Divergences.Add(
                        $"{cachedTask.Id}: cache/DB field divergence (cache status='{cachedTask.Status}' sub='{cachedTask.SubStatus}' vs db status='{dbTask.Status}' sub='{dbTask.SubStatus}')");
                }
            }

            report.Coherent = report.Divergences.Count == 0;
            return report;
        }


        /// <summary>
        /// Add a helper to a task and send Tier 1 notification.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="helperName">Name of the helper terminal to add.</param>
        /// <param name="addedBy">Name of the terminal adding the helper (usually the assignee).</param>
        /// <returns>Result indicating success or failure.</returns>
        public async Task<AddHelperResult> AddHelper(string taskId, string helperName, string addedBy)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new AddHelperResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Check if helper is already added
            if (task.Helpers.Contains(helperName, StringComparer.OrdinalIgnoreCase))
            {
                return new AddHelperResult { Success = false, Error = $"{helperName} is already a helper on this task" };
            }

            var title = task.Title;
            var assignee = task.Assignee;

            // Write path with a custom persist to the task_helpers side table (SaveTask upserts the tasks
            // row, which does NOT carry helpers). The clone's Helpers list gets the new name; on a persist
            // failure the cache keeps the old helper set (coherent) — no hand-rolled in-memory revert.
            if (!TryMutateTask(
                    taskId,
                    t => t.Helpers.Add(helperName),
                    _ =>
                    {
                        var helper = _taskDb.AddTaskHelper(taskId, helperName, addedBy ?? assignee ?? "System");
                        if (helper == null)
                        {
                            throw new InvalidOperationException("AddTaskHelper returned null (helper not persisted)");
                        }
                    }))
            {
                return new AddHelperResult { Success = false, Error = "Failed to persist helper to database" };
            }

            // Send Tier 1 notification to the helper
            await _host.NotifyHelperAdded(helperName, taskId, title, assignee ?? addedBy);

            return new AddHelperResult { Success = true, HelperCount = GetTask(taskId)?.Helpers.Count ?? 0 };
        }


        /// <summary>
        /// Remove a helper from a task.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="helperName">Name of the helper terminal to remove.</param>
        /// <returns>Result indicating success or failure.</returns>
        public RemoveHelperResult RemoveHelper(string taskId, string helperName)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new RemoveHelperResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Find helper (case-insensitive)
            var existingHelper = task.Helpers.FirstOrDefault(h => h.Equals(helperName, StringComparison.OrdinalIgnoreCase));
            if (existingHelper == null)
            {
                return new RemoveHelperResult { Success = false, Error = $"{helperName} is not a helper on this task" };
            }

            var title = task.Title;
            var assignee = task.Assignee;

            // Write path with a custom persist to the task_helpers side table. The clone's Helpers list
            // drops the name; on a persist failure the cache keeps the helper (coherent). This also fixes
            // the pre-P5 ordering (it removed from the DB, then from the cache) into a single atomic swap.
            if (!TryMutateTask(
                    taskId,
                    t => t.Helpers.RemoveAll(h => h.Equals(helperName, StringComparison.OrdinalIgnoreCase)),
                    _ =>
                    {
                        var removed = _taskDb.RemoveTaskHelper(taskId, helperName);
                        if (!removed)
                        {
                            throw new InvalidOperationException("Helper not found in database");
                        }
                    }))
            {
                return new RemoveHelperResult { Success = false, Error = "Failed to remove helper from database" };
            }

            // Record activity
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = assignee ?? "System",
                Type = "helper",
                Action = "removed",
                Content = $"Removed {helperName} as helper from: {title}",
                RelatedId = taskId
            });

            return new RemoveHelperResult { Success = true, HelperCount = GetTask(taskId)?.Helpers.Count ?? 0 };
        }


        /// <summary>
        /// Request help from a helper on a task and send Tier 2 notification.
        /// </summary>
        /// <param name="taskId">The task ID needing help.</param>
        /// <param name="fromTerminal">Terminal name requesting help (usually the assignee).</param>
        /// <param name="helperName">Name of the helper to request help from.</param>
        /// <param name="message">Details about what help is needed.</param>
        /// <returns>Result indicating success or failure.</returns>
        public async Task<RequestHelpResult> RequestHelp(string taskId, string fromTerminal, string helperName, string message)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new RequestHelpResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            // Verify the helper is actually on this task
            if (!task.Helpers.Contains(helperName, StringComparer.OrdinalIgnoreCase))
            {
                return new RequestHelpResult { Success = false, Error = $"{helperName} is not a helper on this task. Add them first." };
            }

            // Send Tier 2 notification (prominent help request)
            var notifyResult = await _host.NotifyHelpRequested(helperName, taskId, task.Title, fromTerminal, message);
            if (!notifyResult.Success)
            {
                return new RequestHelpResult { Success = false, Error = notifyResult.Error };
            }

            // Record activity
            _host.RecordActivity(new ActivityEvent
            {
                Terminal = fromTerminal,
                Type = "help",
                Action = "requested",
                Content = $"Requested help from {helperName} on: {task.Title}",
                RelatedId = taskId
            });

            // Auto-generate inbox notification for helper_request
            try
            {
                _host.CreateInboxNotification(
                    _host.DefaultInboxRecipient,
                    taskId,
                    task.Title,
                    null,
                    null,
                    "helper_request",
                    $"{helperName} needs guidance on '{task.Title}': {message}",
                    fromTerminal);
            }
            catch (Exception ex)
            {
                _host.LogError($"Failed to create helper_request inbox notification: {ex.Message}");
            }

            return new RequestHelpResult { Success = true, Message = $"Help request sent to {helperName}" };
        }


        /// <summary>
        /// Respond to a stale task notification.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="response">Response: 'still_relevant', 'will_close', or 'reprioritize'.</param>
        /// <param name="terminalName">Terminal name responding to the notification.</param>
        /// <returns>Result indicating success or failure.</returns>
        public RespondStaleResult RespondToStale(string taskId, string response, string terminalName)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new RespondStaleResult { Success = false, Error = $"Task not found: {taskId}" };
            }

            var title = task.Title;

            try
            {
                // All branches route through the write path (P5 pipeline C) so cache and DB stay coherent.
                // Pre-P5 this mutated the cached task in place: still_relevant set StaleResponse=response and
                // left StaleNotifiedAt while ClearStaleTracking actually NULLed both in the DB — a divergence.
                KanbanTask updated;
                if (response == "still_relevant")
                {
                    // Clear the stale flag + reset the timer. Mirror EXACTLY what ClearStaleTracking writes
                    // (stale_level=0, flagged_stale_at/stale_notified_at/stale_response = NULL) in the clone.
                    updated = MutateTaskInternal(taskId, t =>
                    {
                        t.StaleLevel = 0;
                        t.FlaggedStaleAt = null;
                        t.StaleNotifiedAt = null;
                        t.StaleResponse = null;
                    }, _ => _taskDb.ClearStaleTracking(taskId));
                }
                else if (response == "reprioritize")
                {
                    // Move back to todo + clear the stale flag; the default SaveTask persist writes the
                    // whole row (including stale_response).
                    updated = MutateTaskInternal(taskId, t =>
                    {
                        t.Status = "todo";
                        t.SubStatus = null;
                        t.PausedAt = null;
                        t.StaleLevel = 0;
                        t.FlaggedStaleAt = null;
                        t.StaleResponse = response;
                    });
                }
                else
                {
                    // will_close (and any other free-text response): keep the flag, just record the response
                    // via RecordStaleResponse (which the clone mirrors into StaleResponse).
                    updated = MutateTaskInternal(taskId, t => t.StaleResponse = response,
                        _ => _taskDb.RecordStaleResponse(taskId, response));
                }

                if (updated == null)
                {
                    return new RespondStaleResult { Success = false, Error = $"Task not found: {taskId}" };
                }

                BroadcastTaskUpdate();

                // Record activity
                _host.RecordActivity(new ActivityEvent
                {
                    Terminal = terminalName,
                    Type = "stale",
                    Action = "responded",
                    Content = $"Responded to stale task '{title}': {response}",
                    RelatedId = taskId
                });

                return new RespondStaleResult { Success = true, Message = $"Response '{response}' recorded" };
            }
            catch (Exception ex)
            {
                return new RespondStaleResult { Success = false, Error = ex.Message };
            }
        }


        /// <summary>
        /// Get helpers for a task.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <returns>List of helper names.</returns>
        public List<string> GetTaskHelpers(string taskId)
        {
            if (_tasks.TryGetValue(taskId, out var task))
            {
                return task.Helpers.ToList();
            }
            return new List<string>();
        }


        /// <summary>
        /// Analyze task complexity to determine if a plan should be suggested.
        /// </summary>
        /// <param name="taskId">The task ID to analyze.</param>
        /// <returns>ComplexityAnalysisResult with score, signals, and recommendation.</returns>
        public ComplexityAnalysisResult AnalyzeTaskComplexity(string taskId)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return new ComplexityAnalysisResult
                {
                    Success = false,
                    Error = $"Task not found: {taskId}"
                };
            }

            if (_host.ComplexityDetector == null)
            {
                return new ComplexityAnalysisResult
                {
                    Success = false,
                    Error = "_host.ComplexityDetector service not available"
                };
            }

            try
            {
                var result = _host.ComplexityDetector.Analyze(task.Title, task.Description);

                return new ComplexityAnalysisResult
                {
                    Success = true,
                    Score = result.Score,
                    SuggestPlan = result.SuggestPlan,
                    SignalsDetected = result.Signals,
                    Recommendation = result.Recommendation
                };
            }
            catch (Exception ex)
            {
                return new ComplexityAnalysisResult
                {
                    Success = false,
                    Error = $"Analysis failed: {ex.Message}"
                };
            }
        }


    }
}
