using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        [JsonConverter(typeof(ChecklistItemNoteListConverter))]
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
        /// Zero-based indices of SIBLING checklist items that must complete before this
        /// one can start. Drives the dependency-graph view (task 60665c6c).
        /// <para>An empty list means "no declared dependencies" — it does NOT mean "depends on
        /// the previous item". Stored array order is presentation order, not a constraint, and
        /// <see cref="MultiTerminal.Services.ChecklistGraphBuilder"/> deliberately emits no edge
        /// for an item with no entries here. Drawing list order as arrows would invent a
        /// constraint chain nobody declared and assert it more confidently than the plan prose
        /// does — that is worse than showing no graph at all.</para>
        /// <para>Agent-authored, so treat as untrusted: out-of-range indices, self-references,
        /// and cycles are all possible and are dropped (with a warning) by the graph builder
        /// rather than throwing.</para>
        /// </summary>
        public List<int> DependsOn { get; set; }

        /// <summary>
        /// Plain-language explanation of this item, written at plan time so a reader can learn
        /// what the step does instead of approving it on trust (task 60665c6c).
        /// <para>Deliberately SEPARATE from <see cref="Notes"/>: notes are reviewer-facing and
        /// carry jargon ("braided methods", "census allowlist"), which is useful but is not
        /// teaching material. Null when no gloss was written — that is a meaningful distinct
        /// state from an empty gloss, so this is NOT defaulted in
        /// <see cref="NormalizeFromLegacy"/>.</para>
        /// </summary>
        public ChecklistItemGloss Gloss { get; set; }

        /// <summary>
        /// Valid statuses for enhanced workflow checklist items.
        /// </summary>
        public static readonly string[] ValidStatuses = { "pending", "coding", "testing", "done" };

        /// <summary>
        /// Normalize a legacy item to the enhanced format.
        /// If Status is null, derives it from the Done flag.
        /// If Notes is null, initializes to empty list.
        /// If DependsOn is null, initializes to empty list (no declared dependencies).
        /// <para><see cref="Gloss"/> is intentionally NOT defaulted — null means "nobody wrote
        /// an explanation", which the graph view renders differently from a written-but-blank one.</para>
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
            if (DependsOn == null)
            {
                DependsOn = new List<int>();
            }
        }
    }

    /// <summary>
    /// Plain-language explanation of a single checklist item, surfaced on hover in the
    /// dependency-graph view (task 60665c6c). Three fields, always rendered in this order,
    /// so a reader learns where to look.
    /// <para>"What it touched" is deliberately absent: it is derived from
    /// <c>task_file_links</c> rows scoped to the item's index, so it is always accurate and
    /// never needs writing by hand.</para>
    /// </summary>
    public class ChecklistItemGloss
    {
        /// <summary>
        /// What this step does, in one plain sentence. No jargon, no internal type names.
        /// </summary>
        public string What { get; set; }

        /// <summary>
        /// Why the step sits where it does in the graph — what it is gated on and why,
        /// or why it is free to run in parallel with its siblings.
        /// </summary>
        public string Why { get; set; }

        /// <summary>
        /// What would go wrong if this step were skipped. This is the field that actually
        /// teaches — "what it is" describes, "without it" explains why anyone bothered.
        /// </summary>
        public string Without { get; set; }

        /// <summary>
        /// True when at least one field carries text. A gloss whose fields are all blank is
        /// treated as absent by the graph view rather than rendered as three empty rows.
        /// </summary>
        public bool HasContent =>
            !string.IsNullOrWhiteSpace(What) ||
            !string.IsNullOrWhiteSpace(Why) ||
            !string.IsNullOrWhiteSpace(Without);
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
    /// Handles legacy checklist JSON where notes may be plain strings instead of objects.
    /// Converts string notes to ChecklistItemNote with the string as Text.
    /// </summary>
    public class ChecklistItemNoteListConverter : JsonConverter<List<ChecklistItemNote>>
    {
        public override List<ChecklistItemNote> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var notes = new List<ChecklistItemNote>();
            if (reader.TokenType != JsonTokenType.StartArray)
                return notes;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    notes.Add(new ChecklistItemNote
                    {
                        By = "unknown",
                        At = DateTime.UtcNow.ToString("o"),
                        Transition = "legacy",
                        Text = reader.GetString() ?? ""
                    });
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var note = JsonSerializer.Deserialize<ChecklistItemNote>(ref reader, options);
                    if (note != null) notes.Add(note);
                }
                else
                {
                    reader.Skip();
                }
            }
            return notes;
        }

        public override void Write(Utf8JsonWriter writer, List<ChecklistItemNote> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
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
