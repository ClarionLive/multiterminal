using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Phase 4 Track 3: periodic reconciliation of <c>task_worktrees</c> rows
    /// against on-disk reality + git branch state. Two passes per sweep:
    ///
    /// <para><b>Pass 1 — reconcile missing-on-disk:</b> rows in
    /// <c>status='active'</c> whose <c>WorktreePath</c> doesn't exist on disk
    /// (manual <c>rm -rf</c>, OS cleanup, drive remount, etc.) get marked
    /// pruned so the panel and the broker stop trying to use them.</para>
    ///
    /// <para><b>Pass 2 — pending-merge detection:</b> rows in
    /// <c>status='pruned'</c> whose owning task is <c>done</c> AND whose
    /// <c>task/{id}</c> branch still exists in git. These are tasks where Phase
    /// 3's auto-merge failed (e.g., conflict) and the dev hasn't resolved yet.
    /// The janitor logs them to the activity feed but does NOT auto-retry —
    /// avoiding infinite-retry loops on persistent conflicts.</para>
    ///
    /// <para><b>Pass 3 — orphan empty-dir sweep:</b> on Windows, when an agent
    /// terminal holds cwd inside a worktree at task-done time,
    /// <see cref="WorktreeManager.PruneForTaskAsync"/> unregisters the worktree
    /// from git but cannot rmdir the directory (OS holds a handle). Result:
    /// an empty directory shell remains on disk, and the next terminal spawned
    /// with that stale path in <c>MULTITERMINAL_TASK_WORKTREE</c> lands in a
    /// non-git dir. Pass 3 enumerates parent dirs that have ever hosted
    /// worktrees (derived from <c>task_worktrees</c>), cross-references
    /// <c>git worktree list --porcelain</c>, and rmdirs any empty children that
    /// git no longer knows about. Non-empty dirs are skipped — Pass 3 never
    /// deletes content.</para>
    ///
    /// <para>Stateless. Decoupled from <see cref="MessageBroker"/> via two
    /// callbacks: <see cref="JanitorActivityCallback"/> for activity logging
    /// and a <see cref="Func{T,TResult}"/> for resolving project paths from
    /// task ids. The wire-up site (typically <c>MainForm</c>) provides both.</para>
    /// </summary>
    public class WorktreeJanitorService
    {
        private readonly TaskDatabase _db;

        public WorktreeJanitorService(TaskDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Activity-log callback signature. Caller wires this to
        /// <c>ActivityService.RecordActivity</c> (or equivalent) without the
        /// janitor needing to depend on the broker / MCPServer namespace.
        /// </summary>
        public delegate void JanitorActivityCallback(string action, string content, string relatedId);

        /// <summary>
        /// Run a single sweep. Safe to call from any thread; performs only
        /// small read+update DB operations. Logs notable events via
        /// <paramref name="recordActivity"/>; logs nothing when the sweep is a
        /// pure no-op (no missing dirs, no pending merges).
        /// </summary>
        /// <param name="getProjectPathForTask">Resolver: taskId → repo root, or
        /// null when the task's project can't be located. Janitor skips Pass 2
        /// rows for which this returns null.</param>
        /// <param name="recordActivity">Activity-feed sink. Pass null to suppress
        /// logging entirely (e.g. headless test runs).</param>
        /// <param name="tryDeferredPruneRetry">Callback to retry a deferred
        /// prune for a taskId (returns true on success). Pass null to disable
        /// the deferred-prune retry pass. Used by the cycle-3 defer-on-timeout
        /// flow: when the broker's pre-prune broadcast didn't complete in time,
        /// the prune is deferred; this callback lets the janitor retry on a
        /// later sweep once agents have likely released their cwd.</param>
        public async Task<JanitorResult> SweepAsync(
            Func<string, string> getProjectPathForTask,
            JanitorActivityCallback recordActivity,
            Func<string, Task<bool>> tryDeferredPruneRetry = null)
        {
            var result = new JanitorResult();

            // Deferred-prune retry pass (cycle-3): tasks whose worktree prune
            // was deferred at task-done time because the broker's pre-prune
            // broadcast timed out. The worktree row is still active even
            // though the task is done. Retry the prune via the supplied
            // callback — by now agents have likely released their cwd, so
            // git worktree remove can succeed cleanly. Skipped when no
            // callback is wired.
            if (tryDeferredPruneRetry != null)
            {
                try
                {
                    var deferredCandidates = _db.ListActiveWorktreesForDoneTasks();
                    foreach (var record in deferredCandidates)
                    {
                        try
                        {
                            bool ok = await tryDeferredPruneRetry(record.TaskId).ConfigureAwait(false);
                            if (ok)
                            {
                                result.DeferredPrunesCompleted++;
                                recordActivity?.Invoke(
                                    "janitor_deferred_prune",
                                    $"Retried deferred prune for task {record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length))}: succeeded.",
                                    record.TaskId);
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Deferred-prune {record.TaskId}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Deferred-prune outer: {ex.Message}");
                }
            }

            // Pass 1: active records whose worktree dir is missing on disk.
            try
            {
                var activeRecords = _db.ListActiveWorktrees();
                foreach (var record in activeRecords)
                {
                    try
                    {
                        if (!Directory.Exists(record.WorktreePath))
                        {
                            _db.MarkWorktreePruned(record.TaskId);
                            result.ReconciledMissing++;
                            recordActivity?.Invoke(
                                "janitor_reconciled_missing",
                                $"Worktree dir missing for task {record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length))}; record marked pruned.",
                                record.TaskId);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Pass 1 reconcile {record.TaskId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Pass 1 outer: {ex.Message}");
            }

            // Pass 2: pruned-but-task-done rows whose branch still exists in git.
            try
            {
                var prunedDone = _db.ListPrunedWorktreesForDoneTasks();
                foreach (var record in prunedDone)
                {
                    try
                    {
                        string projectPath = getProjectPathForTask?.Invoke(record.TaskId);
                        if (string.IsNullOrEmpty(projectPath)) continue;

                        var branchExists = await BranchExistsAsync(projectPath, record.BranchName).ConfigureAwait(false);
                        if (branchExists)
                        {
                            result.PendingMerges.Add(record.TaskId);
                            recordActivity?.Invoke(
                                "janitor_pending_merge",
                                $"Branch {record.BranchName} still alive for done task {record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length))} — merge it manually or re-mark the task done to retry auto-merge.",
                                record.TaskId);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Pass 2 check {record.TaskId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Pass 2 outer: {ex.Message}");
            }

            // Pass 3: orphan empty-dir sweep. Enumerate the parent dirs that
            // have ever hosted a worktree, ask git which children are still
            // registered, and rmdir any empty children git doesn't know about.
            try
            {
                var allPaths = _db.ListAllWorktreePaths();
                var groups = DeriveWorktreeGroups(allPaths);

                // Cycle-2 fix: opportunistically union in legacy parent dirs
                // for every known repo root. After WorktreeLayoutMigrationService
                // rewrites paths from sibling to child layout, the DB no longer
                // references the legacy parent — orphans there would otherwise
                // be permanently invisible to Pass 3. This sweep is conservative
                // (sibling dirs are only added when they exist on disk).
                AppendLegacyParentsFromActiveRepos(groups);

                foreach (var group in groups)
                {
                    if (!Directory.Exists(group.ParentDir)) continue;
                    if (!WorktreeLayout.IsLikelyGitRepoRoot(group.RepoRoot)) continue;

                    HashSet<string> registered;
                    try
                    {
                        registered = await ListRegisteredWorktreePathsAsync(group.RepoRoot).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Conservative: if `git worktree list` failed, skip this
                        // group entirely rather than risk deleting registered dirs.
                        result.Errors.Add($"Pass 3 git-list {group.RepoRoot}: {ex.Message}");
                        continue;
                    }

                    foreach (var child in EnumerateChildren(group.ParentDir))
                    {
                        try
                        {
                            if (registered.Contains(NormalizePath(child))) continue;
                            if (!IsDirectoryEmpty(child)) continue;

                            Directory.Delete(child, recursive: false);
                            result.OrphansRemoved++;
                            recordActivity?.Invoke(
                                "janitor_orphan_removed",
                                $"Removed orphan empty worktree dir: {child}",
                                null);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // Empty-looking but rmdir refused — handle held by
                            // another process (terminal cwd, antivirus). Leave
                            // it; the next sweep will retry.
                            result.Errors.Add($"Pass 3 rmdir {child}: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"Pass 3 child {child}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Pass 3 outer: {ex.Message}");
            }

            // Summary entry — only if anything notable happened.
            if (recordActivity != null
                && (result.ReconciledMissing > 0 || result.PendingMerges.Count > 0 || result.OrphansRemoved > 0 || result.DeferredPrunesCompleted > 0 || result.Errors.Count > 0))
            {
                recordActivity(
                    "janitor_sweep",
                    $"Worktree janitor: reconciled {result.ReconciledMissing} missing, {result.PendingMerges.Count} pending merge, {result.OrphansRemoved} orphans removed, {result.DeferredPrunesCompleted} deferred prunes completed, {result.Errors.Count} errors.",
                    null);
            }

            return result;
        }

        /// <summary>
        /// Group a list of worktree paths by their parent directory and derive
        /// the corresponding repo root for each group via
        /// <see cref="WorktreeLayout.DeriveRepoRootFromParent"/>. Paths whose
        /// parent doesn't match either layout (or whose derived repo root
        /// isn't a git repo) are skipped.
        /// </summary>
        internal static List<WorktreeGroup> DeriveWorktreeGroups(IEnumerable<string> worktreePaths)
        {
            var groups = new Dictionary<string, WorktreeGroup>(StringComparer.OrdinalIgnoreCase);
            if (worktreePaths == null) return new List<WorktreeGroup>();

            foreach (var p in worktreePaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;

                string parentDir;
                try
                {
                    parentDir = Path.GetDirectoryName(p);
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(parentDir)) continue;

                string repoRoot = WorktreeLayout.DeriveRepoRootFromParent(parentDir);
                if (string.IsNullOrEmpty(repoRoot)) continue;

                string key = NormalizePath(parentDir);
                if (!groups.ContainsKey(key))
                {
                    groups[key] = new WorktreeGroup { ParentDir = parentDir, RepoRoot = repoRoot };
                }
            }

            return new List<WorktreeGroup>(groups.Values);
        }

        /// <summary>
        /// Cycle-2 fix: after the migration service rewrites legacy paths to
        /// the new layout, the DB no longer references legacy sibling parents.
        /// For every repo root represented in the current group set, check if
        /// a sibling <c>{repoName}-worktrees</c> dir still exists on disk and
        /// add it as an additional group so orphan empty dirs left behind in
        /// the pre-migration parent get cleaned up. No-op when no repo roots
        /// have been derived yet (e.g. empty DB).
        /// </summary>
        internal static void AppendLegacyParentsFromActiveRepos(List<WorktreeGroup> groups)
        {
            if (groups == null || groups.Count == 0) return;

            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var repoRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groups)
            {
                if (!string.IsNullOrEmpty(g.ParentDir)) existingKeys.Add(NormalizePath(g.ParentDir));
                if (!string.IsNullOrEmpty(g.RepoRoot)) repoRoots.Add(g.RepoRoot);
            }

            foreach (var repoRoot in repoRoots)
            {
                string legacyParent = WorktreeLayout.DeriveLegacyParentForRepo(repoRoot);
                if (string.IsNullOrEmpty(legacyParent)) continue;
                if (!Directory.Exists(legacyParent)) continue;
                if (existingKeys.Contains(NormalizePath(legacyParent))) continue;

                groups.Add(new WorktreeGroup { ParentDir = legacyParent, RepoRoot = repoRoot });
                existingKeys.Add(NormalizePath(legacyParent));
            }
        }

        private static async Task<HashSet<string>> ListRegisteredWorktreePathsAsync(string repoRoot)
        {
            var (exitCode, stdout, stderr) = await RunGitAsync(repoRoot, "worktree", "list", "--porcelain").ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"git worktree list exit {exitCode}: {stderr.Trim()}");
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in stdout.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    paths.Add(NormalizePath(line.Substring("worktree ".Length).Trim()));
                }
            }
            return paths;
        }

        private static IEnumerable<string> EnumerateChildren(string parentDir)
        {
            try
            {
                return Directory.EnumerateDirectories(parentDir);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static bool IsDirectoryEmpty(string dir)
        {
            try
            {
                using var en = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
                return !en.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            try
            {
                return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return p.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Internal pairing of a worktree parent directory with the repo root
        /// whose <c>git worktree list</c> output should be consulted.
        /// </summary>
        internal class WorktreeGroup
        {
            public string ParentDir { get; set; }

            public string RepoRoot { get; set; }
        }

        private static async Task<bool> BranchExistsAsync(string repoRoot, string branchName)
        {
            var (exitCode, stdout, _) = await RunGitAsync(repoRoot, "branch", "--list", branchName).ConfigureAwait(false);
            return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
        }

        private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(
            string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return (proc.ExitCode, stdout, stderr);
        }
    }

    /// <summary>
    /// Outcome of a single janitor sweep.
    /// </summary>
    public class JanitorResult
    {
        public int ReconciledMissing { get; set; }

        public List<string> PendingMerges { get; } = new List<string>();

        /// <summary>Pass 3 — count of empty orphan dirs rmdir'd this sweep.</summary>
        public int OrphansRemoved { get; set; }

        /// <summary>Cycle-3 — count of deferred prunes successfully retried this sweep.</summary>
        public int DeferredPrunesCompleted { get; set; }

        public List<string> Errors { get; } = new List<string>();
    }
}
