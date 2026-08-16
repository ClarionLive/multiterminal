using System;
using System.Collections.Generic;
using System.Text;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Decides WHETHER a gloss backfill is warranted and WHAT to ask for (task a455e295).
    /// <para>Deliberately pure and static — no broker, no DB, no spawning, no clock beyond what is
    /// passed in. The same split as <see cref="MultiTerminal.Services.ChecklistGraphBuilder"/>: the
    /// judgement calls live where they can be tested exhaustively, and the service around this only
    /// does IO. Everything here is a decision; nothing here has an effect.</para>
    /// </summary>
    internal static class GlossBackfillPlanner
    {
        /// <summary>
        /// Upper bound on items handed to one agent. A checklist longer than this is almost
        /// certainly a runaway or a paste accident, and a prompt that large stops producing
        /// coherent per-item prose anyway. Excess items are LEFT for a later pass rather than
        /// silently dropped — the caller logs the shortfall (no silent caps).
        /// </summary>
        public const int MaxItemsPerRun = 25;

        /// <summary>
        /// Indices of items that need an explanation written: the item has real text, and no
        /// gloss carrying content is present.
        /// <para>A blank-but-present gloss counts as absent, matching
        /// <see cref="ChecklistItemGloss.HasContent"/> everywhere else. A gloss already marked
        /// generated counts as PRESENT — re-running would burn tokens rewriting machine prose with
        /// more machine prose, and the point of the backfill is to fill a vacuum, not to iterate.</para>
        /// </summary>
        public static List<int> ItemsNeedingGloss(IReadOnlyList<ChecklistItem> checklist)
        {
            var needed = new List<int>();
            if (checklist == null)
            {
                return needed;
            }

            for (int i = 0; i < checklist.Count; i++)
            {
                var item = checklist[i];
                if (item == null || string.IsNullOrWhiteSpace(item.Item))
                {
                    continue;
                }

                if (item.Gloss == null || !item.Gloss.HasContent)
                {
                    needed.Add(i);
                }
            }

            return needed;
        }

        /// <summary>
        /// Build the instruction handed to the writer agent.
        /// <para>ONE prompt covering every un-glossed item, not one agent per item. Step 3's "why"
        /// routinely refers to what step 2 produced, so an agent shown a single item in isolation
        /// can only reword it — and a reworded item is exactly the failure this feature exists to
        /// avoid: prose that reads like explanation and carries no information.</para>
        /// <para>The instruction to admit ignorance is load-bearing, not politeness. The gloss is
        /// read by someone deciding whether to APPROVE a plan. A confident invented rationale is
        /// worse than a blank, because a blank invites the planner to fill it while a fabrication
        /// hides the gap permanently.</para>
        /// </summary>
        public static string BuildPrompt(KanbanTask task, IReadOnlyList<ChecklistItem> checklist, IReadOnlyList<int> indices)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (checklist == null) throw new ArgumentNullException(nameof(checklist));
            if (indices == null) throw new ArgumentNullException(nameof(indices));

            var sb = new StringBuilder();

            sb.AppendLine("You are writing plain-language explanations for the steps of one work plan, so that");
            sb.AppendLine("a reader can LEARN what the plan does instead of approving it on trust.");
            sb.AppendLine();
            sb.AppendLine($"TASK: {task.Title}");
            sb.AppendLine($"TASK ID: {task.Id}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(task.Description))
            {
                sb.AppendLine("DESCRIPTION:");
                sb.AppendLine(Clip(task.Description, 4000));
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(task.Plan))
            {
                sb.AppendLine("PLAN:");
                sb.AppendLine(Clip(task.Plan, 6000));
                sb.AppendLine();
            }

            sb.AppendLine("FULL CHECKLIST (for context — you write for only the indices listed after it):");
            for (int i = 0; i < checklist.Count; i++)
            {
                var item = checklist[i];
                var mark = item?.Gloss != null && item.Gloss.HasContent ? " (already explained)" : string.Empty;
                sb.AppendLine($"  [{i}] {Clip(item?.Item, 400)}{mark}");
            }
            sb.AppendLine();

            sb.AppendLine($"WRITE A GLOSS FOR THESE INDICES ONLY: {string.Join(", ", indices)}");
            sb.AppendLine("Every other index already has an explanation and must not be touched.");
            sb.AppendLine();
            sb.AppendLine("For each one, call the MCP tool set_checklist_gloss with:");
            sb.AppendLine($"  taskId: \"{task.Id}\"");
            sb.AppendLine("  itemIndex: the index");
            sb.AppendLine("  what:    what the step does, in ONE plain sentence. No jargon, no type or file names.");
            sb.AppendLine("  why:     why it sits where it does — what it is gated on, or why it can run in parallel.");
            sb.AppendLine("  without: what goes wrong if the step is skipped.");
            sb.AppendLine();
            sb.AppendLine("Rules that matter more than finishing:");
            sb.AppendLine();
            sb.AppendLine("1. DO NOT RESTATE THE STEP. If your sentence is the item's own wording with different");
            sb.AppendLine("   words, delete it and write what the step actually accomplishes instead. A paraphrase");
            sb.AppendLine("   reads like an explanation while teaching nothing, which is worse than leaving it blank.");
            sb.AppendLine();
            sb.AppendLine("2. IF YOU CANNOT TELL, SAY SO. Where the plan does not tell you why a step is gated or");
            sb.AppendLine("   what breaks without it, write plainly that it is not stated — e.g. \"The plan does not");
            sb.AppendLine("   say what this is gated on.\" NEVER invent a plausible-sounding rationale. Someone will");
            sb.AppendLine("   approve this plan believing your explanation; an honest gap gets filled by the planner,");
            sb.AppendLine("   an invented one is believed and never corrected.");
            sb.AppendLine();
            sb.AppendLine("3. \"without\" is the field that teaches. Spend your effort there.");
            sb.AppendLine();
            sb.AppendLine("4. Write for someone who does not know this codebase. No internal vocabulary.");
            sb.AppendLine();
            sb.AppendLine("Do not change any status, notes, or anything else about the task. Writing the glosses is");
            sb.AppendLine("your whole job. When every listed index is written, reply with one line saying how many");
            sb.AppendLine("you wrote and stop.");

            return sb.ToString();
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "\n…(truncated)";
        }
    }
}
