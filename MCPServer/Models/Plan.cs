using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents a Plan - the central organizing concept for task-centric workflow.
    /// A Plan tracks a goal from design through completion with phases and assignments.
    /// </summary>
    public class Plan
    {
        /// <summary>
        /// Unique plan identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Plan title - short description of the goal.
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Detailed description of what this plan aims to accomplish.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Full plan content - the detailed implementation plan text.
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Current phase: design, coding, testing, completed
        /// </summary>
        public string CurrentPhase { get; set; } = "design";

        /// <summary>
        /// Plan status: draft, active, paused, completed, abandoned
        /// Only one plan can be 'active' at a time.
        /// </summary>
        public string Status { get; set; } = "draft";

        /// <summary>
        /// Terminal name of the plan leader.
        /// </summary>
        public string LeaderId { get; set; }

        /// <summary>
        /// When the plan was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the plan was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Valid phases for a plan.
        /// </summary>
        public static readonly string[] ValidPhases = { "design", "coding", "testing", "completed" };

        /// <summary>
        /// Valid statuses for a plan.
        /// </summary>
        public static readonly string[] ValidStatuses = { "draft", "active", "paused", "completed", "abandoned" };
    }

    /// <summary>
    /// Represents a phase within a plan with its checklist items.
    /// </summary>
    public class PlanPhase
    {
        /// <summary>
        /// Unique phase identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// The plan this phase belongs to.
        /// </summary>
        public string PlanId { get; set; }

        /// <summary>
        /// Phase name: design, coding, testing, completed
        /// </summary>
        public string PhaseName { get; set; }

        /// <summary>
        /// Phase order (1=design, 2=coding, 3=testing, 4=completed)
        /// </summary>
        public int PhaseOrder { get; set; }

        /// <summary>
        /// JSON array of checklist items: [{"item": "Create schema", "done": true}, ...]
        /// </summary>
        public string ChecklistJson { get; set; } = "[]";

        /// <summary>
        /// When this phase was started.
        /// </summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>
        /// When this phase was completed.
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Get checklist as list of items.
        /// </summary>
        public List<ChecklistItem> GetChecklist()
        {
            if (string.IsNullOrEmpty(ChecklistJson)) return new List<ChecklistItem>();
            return JsonSerializer.Deserialize<List<ChecklistItem>>(ChecklistJson) ?? new List<ChecklistItem>();
        }

        /// <summary>
        /// Set checklist from list of items.
        /// </summary>
        public void SetChecklist(List<ChecklistItem> items)
        {
            ChecklistJson = JsonSerializer.Serialize(items);
        }
    }

    /// <summary>
    /// A single checklist item within a phase or kanban task.
    /// Supports both legacy format (Item + Done) and enhanced workflow format (Status + Notes).
    /// </summary>
    public class ChecklistItem
    {
        /// <summary>
        /// Description of the checklist item.
        /// </summary>
        public string Item { get; set; }

        /// <summary>
        /// Legacy boolean completion flag. Kept for backwards compatibility.
        /// New code should use Status instead.
        /// </summary>
        public bool Done { get; set; }

        /// <summary>
        /// Workflow status: "pending", "coding", "testing", "done".
        /// Null for legacy items (use Done flag instead).
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Transition notes history - a conversation trail of coding/testing cycles.
        /// Each entry records who made the transition, when, and what was done/found.
        /// </summary>
        public List<ChecklistItemNote> Notes { get; set; }

        /// <summary>
        /// Agent assigned to this checklist item (for helper assignment).
        /// Null means assigned to the task's primary assignee.
        /// </summary>
        public string AssignedTo { get; set; }

        /// <summary>
        /// Number of coding↔testing cycles this item has gone through.
        /// Auto-incremented on each testing→coding transition.
        /// At 4+ cycles, triggers escalation.
        /// </summary>
        public int CycleCount { get; set; }

        /// <summary>
        /// Sort order within the lifecycle column. Used by the lifecycle board
        /// to persist card positions within each column.
        /// </summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// Valid statuses for enhanced workflow checklist items.
        /// </summary>
        public static readonly string[] ValidStatuses = { "pending", "coding", "testing", "done" };

        /// <summary>
        /// Normalize a legacy item to the enhanced format.
        /// If Status is null, derives it from the Done flag.
        /// If Notes is null, initializes to empty list.
        /// </summary>
        public void NormalizeFromLegacy()
        {
            if (Status == null)
            {
                Status = Done ? "done" : "pending";
            }
            if (Notes == null)
            {
                Notes = new List<ChecklistItemNote>();
            }
        }
    }

    /// <summary>
    /// A note recorded during a checklist item status transition.
    /// Builds a conversation trail between agents and reviewers.
    /// </summary>
    public class ChecklistItemNote
    {
        /// <summary>
        /// Who made this transition (agent name or user name).
        /// </summary>
        public string By { get; set; }

        /// <summary>
        /// ISO 8601 timestamp of when the transition occurred.
        /// </summary>
        public string At { get; set; }

        /// <summary>
        /// Description of the transition, e.g. "coding → testing" or "testing → coding".
        /// </summary>
        public string Transition { get; set; }

        /// <summary>
        /// The note content - what was done, what needs fixing, confirmation, etc.
        /// </summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// Represents a terminal's assignment within a plan.
    /// </summary>
    public class PlanAssignment
    {
        /// <summary>
        /// Unique assignment identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// The plan this assignment belongs to.
        /// </summary>
        public string PlanId { get; set; }

        /// <summary>
        /// Terminal name assigned to this task.
        /// </summary>
        public string TerminalName { get; set; }

        /// <summary>
        /// Role in this plan: leader or member
        /// </summary>
        public string Role { get; set; } = "member";

        /// <summary>
        /// Summary of what this terminal is assigned to do.
        /// </summary>
        public string AssignedTaskSummary { get; set; }

        /// <summary>
        /// Assignment status: assigned, in_progress, blocked, done
        /// </summary>
        public string Status { get; set; } = "assigned";

        /// <summary>
        /// What this assignment is blocked by (if status is blocked).
        /// </summary>
        public string BlockedBy { get; set; }

        /// <summary>
        /// When this assignment was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Valid assignment statuses.
        /// </summary>
        public static readonly string[] ValidStatuses = { "assigned", "in_progress", "blocked", "done" };
    }

    /// <summary>
    /// Records a decision made during plan execution for future context.
    /// </summary>
    public class PlanDecision
    {
        /// <summary>
        /// Unique decision identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// The plan this decision belongs to.
        /// </summary>
        public string PlanId { get; set; }

        /// <summary>
        /// Phase when this decision was made.
        /// </summary>
        public string Phase { get; set; }

        /// <summary>
        /// The decision that was made.
        /// </summary>
        public string DecisionText { get; set; }

        /// <summary>
        /// Rationale for the decision.
        /// </summary>
        public string Rationale { get; set; }

        /// <summary>
        /// Terminal that made/recorded the decision.
        /// </summary>
        public string DecidedBy { get; set; }

        /// <summary>
        /// When the decision was recorded.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
