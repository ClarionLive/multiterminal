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
    }
}
