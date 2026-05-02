using System;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Static configuration holder for Phase 1 per-task worktree isolation
    /// (task e1a5c579). Reads <c>MULTITERMINAL_WORKTREE_MODE</c> from the
    /// environment exactly once at process startup and exposes the result
    /// via <see cref="IsEnabled"/>.
    ///
    /// <para>Default is <c>off</c>. Only the literal value <c>"on"</c>
    /// (case-insensitive, trimmed) enables the lifecycle hooks; everything
    /// else — including unset, blank, "1", "true", "yes" — leaves them off.
    /// This keeps the toggle unambiguous during the spike.</para>
    /// </summary>
    public static class WorktreeConfig
    {
        /// <summary>
        /// Name of the env var that gates Phase 1 lifecycle hooks. Set to
        /// <c>"on"</c> to enable worktree create/prune on task activate/done.
        /// </summary>
        public const string ModeEnvVar = "MULTITERMINAL_WORKTREE_MODE";

        /// <summary>
        /// Name of the env var that <see cref="TerminalSpawner"/> and
        /// <see cref="AgentProcess"/> inject into spawned agents to
        /// communicate the worktree path. Empty when no worktree is in play.
        /// </summary>
        public const string TaskWorktreeEnvVar = "MULTITERMINAL_TASK_WORKTREE";

        /// <summary>
        /// Resolved once at first access. <c>true</c> when
        /// <c>MULTITERMINAL_WORKTREE_MODE=on</c> at process start.
        /// </summary>
        public static bool IsEnabled { get; } = ResolveEnabled();

        private static bool ResolveEnabled()
        {
            string raw = Environment.GetEnvironmentVariable(ModeEnvVar);
            return string.Equals(raw?.Trim(), "on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
