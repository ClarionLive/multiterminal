using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Records a git worktree associated with a kanban task. Created when a
    /// task is set active (under MULTITERMINAL_WORKTREE_MODE=on) and marked
    /// pruned when the task moves to status='done' and the worktree directory
    /// is removed. Phase 1 of per-task worktree isolation (task e1a5c579).
    /// </summary>
    public class TaskWorktree
    {
        public string TaskId { get; set; }

        public string WorktreePath { get; set; }

        public string BranchName { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// "active" while the worktree is materialized on disk; "pruned"
        /// after <c>git worktree remove</c> succeeds. Records are retained
        /// post-prune for audit / history.
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Terminal/agent name that owns this worktree. Part of the composite
        /// key <c>(TaskId, AgentName)</c> introduced for per-agent worktree
        /// isolation (task bab81a92 / design ff1dc68f): the assignee holds the
        /// canonical worktree while each helper gets their own. Legacy rows
        /// (pre-migration) backfill to the task's assignee or <c>"__legacy__"</c>.
        /// </summary>
        public string AgentName { get; set; }

        /// <summary>
        /// True for the task's canonical worktree (the assignee's, on branch
        /// <c>task/&lt;id&gt;</c>); false for a helper worktree (on branch
        /// <c>task/&lt;id&gt;--&lt;slug&gt;</c>). The canonical worktree is the
        /// integration target at task-done and the source of the trunk merge.
        /// </summary>
        public bool IsCanonical { get; set; }
    }
}
