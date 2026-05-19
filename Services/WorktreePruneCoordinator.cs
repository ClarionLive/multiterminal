using System;
using System.Collections.Generic;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Process-wide registry of worktree paths that are currently being
    /// pruned. Lets <see cref="TerminalSpawner"/> refuse to launch a new
    /// terminal targeting a worktree whose <c>git worktree remove</c> is
    /// already in flight — closes the TOCTOU window between
    /// <see cref="WorktreeManager.PruneForTaskAsync"/> deciding to prune and
    /// actually unregistering the path from git.
    ///
    /// <para>Task db4b18c6 cycle 2: cross-model-adversary flagged that
    /// <see cref="TerminalSpawner.IsValidWorktree"/> succeeds during the
    /// short window after the broker has resolved <c>PruneForTaskAsync</c>
    /// but before git has finished removing the dir. A spawn racing through
    /// that window lands in a soon-to-be-deleted path. The coordinator
    /// supplies the visibility that's missing.</para>
    ///
    /// <para>Thread-safe via internal lock. Static for simplicity — the
    /// pruning lifetime is process-scoped (broker → spawner are in the same
    /// process) and there is no need for DI plumbing.</para>
    /// </summary>
    internal static class WorktreePruneCoordinator
    {
        private static readonly object _lock = new object();
        private static readonly HashSet<string> _pruning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Marks a worktree path as being pruned. Idempotent — multiple
        /// concurrent calls for the same path are safe. Pair with
        /// <see cref="UnmarkPruning"/> in a try/finally around the actual
        /// <c>git worktree remove</c> call.
        /// </summary>
        public static void MarkPruning(string worktreePath)
        {
            if (string.IsNullOrWhiteSpace(worktreePath)) return;
            string key = Normalize(worktreePath);
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                _pruning.Add(key);
            }
        }

        /// <summary>
        /// Removes a worktree path from the pruning set. Safe to call when
        /// the path isn't marked (no-op).
        /// </summary>
        public static void UnmarkPruning(string worktreePath)
        {
            if (string.IsNullOrWhiteSpace(worktreePath)) return;
            string key = Normalize(worktreePath);
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                _pruning.Remove(key);
            }
        }

        /// <summary>
        /// Returns <c>true</c> when the worktree path is currently being
        /// pruned by another thread. Callers use this to refuse / reroute
        /// spawns targeting an in-flight prune target.
        /// </summary>
        public static bool IsPruning(string worktreePath)
        {
            if (string.IsNullOrWhiteSpace(worktreePath)) return false;
            string key = Normalize(worktreePath);
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                return _pruning.Contains(key);
            }
        }

        private static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try
            {
                return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
    }
}
