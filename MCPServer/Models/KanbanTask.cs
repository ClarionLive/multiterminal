using System;
using System.Collections.Generic;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents a task on the shared Kanban board.
    /// </summary>
    public class KanbanTask
    {
        /// <summary>
        /// Unique task identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Task title.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Detailed task description.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Task status: "todo", "in_progress", or "done".
        /// </summary>
        public string Status { get; set; } = "todo";

        /// <summary>
        /// Terminal name that claimed this task (null if unclaimed).
        /// </summary>
        public string Assignee { get; set; }

        /// <summary>
        /// Terminal name that created this task.
        /// </summary>
        public string CreatedBy { get; set; }

        /// <summary>
        /// When the task was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Project ID this task belongs to (null if not associated with a project).
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// Sub-status for in_progress tasks: "active", "paused", or null.
        /// </summary>
        public string SubStatus { get; set; }

        /// <summary>
        /// When the task was paused (null if not paused).
        /// </summary>
        public DateTime? PausedAt { get; set; }

        /// <summary>
        /// When the task was flagged as stale (null if not flagged).
        /// </summary>
        public DateTime? FlaggedStaleAt { get; set; }

        /// <summary>
        /// Stale level: 0=fresh, 1=day7-flagged, 2=day14-flagged.
        /// Used to track aging of paused tasks.
        /// </summary>
        public int StaleLevel { get; set; }

        /// <summary>
        /// When the last stale notification was sent.
        /// </summary>
        public DateTime? StaleNotifiedAt { get; set; }

        /// <summary>
        /// Response to stale notification: 'still_relevant', 'will_close', 'reprioritized', or null.
        /// </summary>
        public string StaleResponse { get; set; }

        /// <summary>
        /// List of terminal names assigned as helpers on this task.
        /// Helpers receive Tier 1 notifications when added and can receive
        /// Tier 2 help requests from the assignee.
        /// </summary>
        public List<string> Helpers { get; set; } = new List<string>();

        /// <summary>
        /// Task priority level: "urgent", "normal", or "low".
        /// Determines auto-pause behavior when task is assigned.
        /// - urgent: Interrupts current work after grace period
        /// - normal: Queues behind current task (default)
        /// - low: Added to backlog, doesn't interrupt
        /// </summary>
        public string Priority { get; set; } = "normal";

        /// <summary>
        /// JSON array of checklist items: [{"item": "Setup database", "done": true}, ...]
        /// Used to track sub-tasks within a card.
        /// </summary>
        public string ChecklistJson { get; set; } = "[]";

        /// <summary>
        /// Implementation plan (markdown formatted).
        /// Describes the approach and steps for completing this task.
        /// </summary>
        public string Plan { get; set; }

        /// <summary>
        /// Implementation summary (markdown formatted).
        /// Documents what was actually built/changed when completing this task.
        /// </summary>
        public string ImplementationSummary { get; set; }

        /// <summary>
        /// Test results (markdown formatted).
        /// Documents build verification, test outcomes, and quality checks.
        /// </summary>
        public string TestResults { get; set; }

        /// <summary>
        /// JSON array of implementation checklist items: [{"item": "Created TaskDatabase migration", "done": true}, ...]
        /// Used to track what was actually built/implemented during task completion.
        /// Separate from general ChecklistJson which tracks sub-tasks to do.
        /// </summary>
        public string ImplementationChecklistJson { get; set; } = "[]";

        /// <summary>
        /// Quick "pick up here" context for session handoffs.
        /// Auto-written on checklist item transitions and session end.
        /// Read by startup hook to inject continuation context.
        /// Separate from TaskSummary which provides detailed historical records.
        /// </summary>
        public string ContinuationNotes { get; set; }

        /// <summary>
        /// JSON array of inline code review notes from the human reviewer.
        /// Each note: { file, line, lineContent, severity, comment, timestamp }.
        /// Severity: BLOCKER, SUGGESTION, NITPICK, QUESTION.
        /// Written when the dev submits notes from the diff viewer; cleared on next pass.
        /// </summary>
        public string ReviewNotes { get; set; }

        /// <summary>
        /// When true, this task is a "quick task" — a lightweight, immutable attribution
        /// anchor for trivial working-tree changes (typos, version bumps, changelog catchups)
        /// that don't warrant a full kanban card with checklist or lifecycle. Created via
        /// POST /api/tasks/quick at status='done'; the server rejects subsequent status,
        /// plan, and checklist mutations (only title edits allowed). Hidden by default from
        /// list_tasks unless includeQuickTasks=true (Git tab + Quick-Tasks audit view).
        /// See task d42423e3.
        /// </summary>
        public bool IsQuickTask { get; set; }

        /// <summary>
        /// Manual sort order within the task's status column. Lower values render first.
        /// Gap-based (1000-unit increments at seed time), midpoint-inserted on drag-rank.
        /// Auto-rebalanced when the minimum gap between adjacent siblings shrinks below epsilon.
        /// Null only on rows that pre-date the migration AND were never reordered;
        /// such rows sort last via NULLS LAST in list queries (see TaskDatabase.LoadAllTasks).
        /// </summary>
        public double? SortOrder { get; set; }

        /// <summary>
        /// When true, parent task status is auto-derived from checklist item positions:
        /// - All in Planning → "todo"
        /// - Any in Coding/Testing → "in_progress"
        /// - All in Done → "done"
        /// When false (manual override), user controls status directly.
        /// Default: true for tasks opened in the lifecycle board.
        /// </summary>
        public bool AutoStatus { get; set; }

        /// <summary>
        /// Returns a copy of this task with an independent <see cref="Helpers"/> list.
        /// <para>Used by MessageBroker's single task write path (P5 / ticket 1df2a534): a mutation is
        /// applied to a clone, persisted, and only THEN swapped into the cache. That ordering means a
        /// persist failure cannot leave the cache diverged from the DB, and a concurrent reader never
        /// observes a half-mutated task — it sees either the old or the new copy, each internally whole.</para>
        /// <para>Every other property is a string or a value type (immutable, or copied by value by
        /// <see cref="object.MemberwiseClone"/>), so a member-wise copy plus a fresh <see cref="Helpers"/>
        /// list is a sufficient deep copy. If a future field is a mutable reference type, deep-copy it here.</para>
        /// </summary>
        public KanbanTask Clone()
        {
            var copy = (KanbanTask)MemberwiseClone();
            copy.Helpers = Helpers == null ? null : new List<string>(Helpers);
            return copy;
        }

        /// <summary>
        /// Get checklist as list of items, normalizing any legacy items to enhanced format.
        /// </summary>
        public List<ChecklistItem> GetChecklist()
        {
            if (string.IsNullOrEmpty(ChecklistJson)) return new List<ChecklistItem>();
            var items = System.Text.Json.JsonSerializer.Deserialize<List<ChecklistItem>>(ChecklistJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ChecklistItem>();
            foreach (var item in items)
            {
                item.NormalizeFromLegacy();
            }
            return items;
        }

        /// <summary>
        /// Set checklist from list of items.
        /// Syncs the legacy Done flag with Status for backwards compatibility.
        /// </summary>
        public void SetChecklist(List<ChecklistItem> items)
        {
            foreach (var item in items)
            {
                item.NormalizeFromLegacy();
                item.Done = item.Status == "done";
            }
            ChecklistJson = System.Text.Json.JsonSerializer.Serialize(items,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }

        /// <summary>
        /// Get implementation checklist as list of items.
        /// </summary>
        public List<ChecklistItem> GetImplementationChecklist()
        {
            if (string.IsNullOrEmpty(ImplementationChecklistJson)) return new List<ChecklistItem>();
            return System.Text.Json.JsonSerializer.Deserialize<List<ChecklistItem>>(ImplementationChecklistJson) ?? new List<ChecklistItem>();
        }

        /// <summary>
        /// Set implementation checklist from list of items.
        /// </summary>
        public void SetImplementationChecklist(List<ChecklistItem> items)
        {
            ImplementationChecklistJson = System.Text.Json.JsonSerializer.Serialize(items);
        }
    }

    /// <summary>
    /// Result of creating a task.
    /// </summary>
    public class CreateTaskResult
    {
        public bool Success { get; set; }
        public string TaskId { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of a debug cache-coherency check (P5 / ticket 1df2a534): whether the in-memory task cache
    /// agreed with the DB rows for the sampled tasks. Under the single write path this should always be
    /// coherent — it's the observable evidence that clone→persist→swap keeps <c>_tasks</c> in lockstep
    /// with the tasks table. Any entry in <see cref="Divergences"/> is a coherency bug.
    /// </summary>
    public class CacheCoherencyReport
    {
        public bool Coherent { get; set; }
        public int CachedCount { get; set; }
        public int Checked { get; set; }
        public List<string> Divergences { get; set; } = new List<string>();
    }

    /// <summary>
    /// Result of claiming a task.
    /// </summary>
    public class ClaimTaskResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// Optional complexity analysis suggestion when claiming a task.
        /// Populated if the task is detected as complex.
        /// </summary>
        public ComplexitySuggestion ComplexitySuggestion { get; set; }

        /// <summary>
        /// True if the task was queued behind active work rather than becoming immediately active.
        /// This happens with normal/low priority tasks when the terminal already has active work.
        /// </summary>
        public bool WasQueued { get; set; }

        /// <summary>
        /// Title of the active task that this task is queued behind, if WasQueued is true.
        /// </summary>
        public string QueuedBehind { get; set; }
    }

    /// <summary>
    /// Complexity suggestion included with claim_task results.
    /// </summary>
    public class ComplexitySuggestion
    {
        /// <summary>
        /// Complexity score (0-100).
        /// </summary>
        public int Score { get; set; }

        /// <summary>
        /// True if a plan is recommended for this task.
        /// </summary>
        public bool SuggestPlan { get; set; }

        /// <summary>
        /// List of detected complexity signals.
        /// </summary>
        public List<string> Signals { get; set; }

        /// <summary>
        /// Human-readable recommendation.
        /// </summary>
        public string Recommendation { get; set; }
    }

    /// <summary>
    /// Result of analyzing task complexity.
    /// </summary>
    public class ComplexityAnalysisResult
    {
        public bool Success { get; set; }
        public int Score { get; set; }
        public bool SuggestPlan { get; set; }
        public List<string> SignalsDetected { get; set; }
        public string Recommendation { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of generating a plan suggestion for a task.
    /// </summary>
    public class PlanSuggestionResult
    {
        public bool Success { get; set; }
        public string TaskId { get; set; }
        public string TaskTitle { get; set; }
        public List<SuggestedPhase> SuggestedPhases { get; set; }
        public string Rationale { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// A suggested phase for a plan.
    /// </summary>
    public class SuggestedPhase
    {
        public int Order { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Result of updating task status.
    /// </summary>
    public class UpdateTaskStatusResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of updating a task's title/description.
    /// </summary>
    public class UpdateTaskResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of deleting a task.
    /// </summary>
    public class DeleteTaskResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of adding a helper to a task.
    /// </summary>
    public class AddHelperResult
    {
        public bool Success { get; set; }
        public int HelperCount { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of removing a helper from a task.
    /// </summary>
    public class RemoveHelperResult
    {
        public bool Success { get; set; }
        public int HelperCount { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of getting helpers for a task.
    /// </summary>
    public class GetHelpersResult
    {
        public bool Success { get; set; }
        public List<string> Helpers { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of requesting help from a helper.
    /// </summary>
    public class RequestHelpResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of responding to a stale task notification.
    /// </summary>
    public class RespondStaleResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of transitioning a checklist item status.
    /// </summary>
    public class UpdateChecklistItemResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string ItemName { get; set; }
        public string PreviousStatus { get; set; }
        public string NewStatus { get; set; }
        public int CycleCount { get; set; }
        public bool EscalationTriggered { get; set; }
    }

    /// <summary>
    /// Result of setting a task active with auto-pause.
    /// </summary>
    public class SetTaskActiveResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<string> PausedTaskIds { get; set; } = new List<string>();
        public List<string> PausedTaskTitles { get; set; } = new List<string>();
    }

    /// <summary>
    /// Result of updating continuation notes.
    /// </summary>
    public class UpdateContinuationResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public class TaskRelationship
    {
        public string Id { get; set; }
        public string SourceTaskId { get; set; }
        public string TargetTaskId { get; set; }
        public string Type { get; set; } // "blocks", "depends_on", "related_to"
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class AddRelationshipResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public class RemoveRelationshipResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public class GetRelationshipsResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<TaskRelationship> Relationships { get; set; } = new List<TaskRelationship>();
    }

    public class TaskFileLink
    {
        public string Id { get; set; }
        public string TaskId { get; set; }
        public string FilePath { get; set; }
        public string Description { get; set; }
        public int? LineStart { get; set; }
        public int? LineEnd { get; set; }
        public string AddedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        // NULL = task-scoped (default, applies to all items). Index = item-scoped
        // (only that checklist item touches this file). Drives per-item review-note
        // routing in HandleCodeReviewVerdict (task 87ee90c3).
        public int? ChecklistItemIndex { get; set; }
    }

    public class LinkFileResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public int FileCount { get; set; }
    }

    public class UnlinkFileResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public class GetTaskFilesResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<TaskFileLink> Files { get; set; } = new List<TaskFileLink>();
    }
}
