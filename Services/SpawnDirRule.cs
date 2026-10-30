using System;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Where a named agent's launch starts when that NAME has an active task with a worktree (the AC7
    /// launch-root rule, task c6ed236c, with the explicit-target guard of task f1f74a8f). Pure.
    /// <para>Names repeat across projects and days, so the task found by name may belong to a different
    /// agent in a different repo. Ticket 28d71d14: Grace spawned helper Quinn in ClarionClone; an older
    /// agent also called Quinn had an active MultiTerminal task, and the launch was moved into the
    /// MultiTerminal repo, where the new Quinn then worked for two days. So a launch whose folder was
    /// named explicitly is only moved to a task whose repo contains that folder.</para>
    /// </summary>
    public static class SpawnDirRule
    {
        /// <summary>
        /// The decision: <see cref="Dir"/> to start in, <see cref="TaskWorktreePath"/> for the in-shell cd
        /// (null when none applies), and <see cref="TaskInAnotherRepo"/> when a task worktree existed but
        /// was set aside because its repo does not contain the named folder.
        /// </summary>
        public readonly struct Decision
        {
            public Decision(string dir, string taskWorktreePath, bool taskInAnotherRepo)
            {
                Dir = dir;
                TaskWorktreePath = taskWorktreePath;
                TaskInAnotherRepo = taskInAnotherRepo;
            }

            public string Dir { get; }
            public string TaskWorktreePath { get; }
            public bool TaskInAnotherRepo { get; }
        }

        /// <param name="requestedDir">The folder the launch would use without the task.</param>
        /// <param name="requestedExplicitly">Whether the caller NAMED that folder (a spawn's working
        /// folder, a project card, a folder picker) rather than it being a default.</param>
        /// <param name="taskWorktreePath">The name's active-task worktree, or null.</param>
        /// <param name="taskRepoRoot">That task's repo root, or null when it could not be resolved.</param>
        /// <param name="directoryExists">Whether a folder exists (injected for tests).</param>
        public static Decision Decide(string requestedDir, bool requestedExplicitly, string taskWorktreePath, string taskRepoRoot, Func<string, bool> directoryExists)
        {
            if (string.IsNullOrEmpty(taskWorktreePath))
                return new Decision(requestedDir, null, false);

            // The worktree and the root come from two lookups by name, so they can disagree if the name's
            // task changed in between (Codex, 28d71d14). A root that does not contain the worktree is not
            // that worktree's repo: the worktree itself is then the anchor and the start folder.
            bool repoRootUsable = !string.IsNullOrEmpty(taskRepoRoot) && directoryExists(taskRepoRoot)
                && LaunchCommandBuilder.IsWithinRepo(taskWorktreePath, taskRepoRoot);

            if (requestedExplicitly)
            {
                // The repo root when it resolved; otherwise the worktree is the best anchor for the task's repo.
                string taskRepoAnchor = repoRootUsable ? taskRepoRoot : taskWorktreePath;
                if (!LaunchCommandBuilder.IsWithinRepo(requestedDir, taskRepoAnchor))
                    return new Decision(requestedDir, null, true);
            }

            return new Decision(repoRootUsable ? taskRepoRoot : taskWorktreePath, taskWorktreePath, false);
        }
    }
}
