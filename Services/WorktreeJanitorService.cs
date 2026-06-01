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
    /// <para><b>Pass 2 — pending-merge detection + bounded recovery:</b> rows in
    /// <c>status='pruned'</c> whose owning task is <c>done</c> AND whose
    /// <c>task/{id}</c> branch still exists in git. These are tasks where Phase
    /// 3's auto-merge failed and the dev hasn't resolved yet. When a
    /// <c>tryMergeForTask</c> callback is supplied, the janitor attempts the
    /// merge ONCE per sweep (task d75d7d6e) — bounded, not a loop — so branches
    /// orphaned by the now-fixed dirty-trunk blocker self-heal. A genuine
    /// conflict clean-aborts inside the merge service and the branch stays
    /// flagged to the activity feed for manual resolution.</para>
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
        /// <param name="tryMergeForTask">Callback (taskId, repoRoot) → MergeResult
        /// that retries the Phase-3 auto-merge for a pending-merge branch. Pass
        /// null to keep Pass 2 in flag-only mode. When wired, Pass 2 attempts
        /// the merge ONCE per sweep (task d75d7d6e): now that the common
        /// dirty-trunk blocker auto-resolves, most pending merges succeed on
        /// retry; a genuine conflict clean-aborts inside the merge service and
        /// the branch stays flagged — bounded per sweep, never a retry loop.</param>
        public async Task<JanitorResult> SweepAsync(
            Func<string, string> getProjectPathForTask,
            JanitorActivityCallback recordActivity,
            Func<string, Task<bool>> tryDeferredPruneRetry = null,
            Func<string, string, Task<MergeResult>> tryMergeForTask = null)
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
                            // Per-agent isolation: mark only THIS agent's row pruned,
                            // not every worktree row for the task (a task may now have
                            // multiple agent worktrees).
                            _db.MarkWorktreePruned(record.TaskId, record.AgentName);
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
                            // Bounded one-shot recovery: attempt the auto-merge
                            // once this sweep. With the dirty-trunk blocker now
                            // auto-resolved (task d75d7d6e), most orphaned
                            // branches merge cleanly on retry. A real conflict
                            // clean-aborts inside the merge service and we fall
                            // through to flagging — no retry loop.
                            if (tryMergeForTask != null)
                            {
                                MergeResult retry = null;
                                try
                                {
                                    retry = await tryMergeForTask(record.TaskId, projectPath).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    result.Errors.Add($"Pass 2 merge-retry {record.TaskId}: {ex.Message}");
                                }

                                if (retry != null && retry.Success)
                                {
                                    result.MergesRecovered++;
                                    recordActivity?.Invoke(
                                        "janitor_merge_recovered",
                                        $"Auto-merge recovered for done task {record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length))}: branch {record.BranchName} merged into {retry.MergedInto ?? "trunk"} and deleted.",
                                        record.TaskId);
                                    continue;
                                }
                            }

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
                            // it; the next sweep will retry. Count it as a strand
                            // that did NOT self-heal this round so the sweep
                            // summary can surface it (task 248cc2ce).
                            result.StrandedDirsRemaining++;
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

            // Summary entry — only if anything notable happened. StrandedDirsRemaining
            // is included in the notable test (task 248cc2ce) so a wedged strand that
            // persists across sweeps stays visible even on an otherwise-quiet round.
            if (recordActivity != null
                && (result.ReconciledMissing > 0 || result.PendingMerges.Count > 0 || result.OrphansRemoved > 0 || result.DeferredPrunesCompleted > 0 || result.MergesRecovered > 0 || result.StrandedDirsRemaining > 0 || result.Errors.Count > 0))
            {
                recordActivity(
                    "janitor_sweep",
                    $"Worktree janitor: reconciled {result.ReconciledMissing} missing, {result.MergesRecovered} merges recovered, {result.PendingMerges.Count} pending merge, {result.OrphansRemoved} orphans removed, {result.StrandedDirsRemaining} rmdir-blocked this sweep (held — not a live total; query api/worktrees/stranded for that), {result.DeferredPrunesCompleted} deferred prunes completed, {result.Errors.Count} errors.",
                    null);
            }

            return result;
        }

        /// <summary>
        /// Read-only scan (task 248cc2ce): enumerate the parent dirs that have
        /// ever hosted a worktree, ask git which children are still registered,
        /// and return any empty children git no longer knows about — i.e. the
        /// de-registered-but-on-disk strands — WITHOUT deleting anything. Shares
        /// the same detection primitives as Pass 3 of <see cref="SweepAsync"/>;
        /// backs the on-demand stranded-dir count surface (REST + HUD) so
        /// reliability is observable rather than discovered by stumbling on an
        /// orphan dir. Best-effort: a group whose <c>git worktree list</c> fails
        /// is skipped (conservative — never reports a registered dir as stranded).
        /// </summary>
        public async Task<StrandedScanResult> ScanStrandedDirsAsync()
        {
            var result = new StrandedScanResult();
            var allPaths = _db.ListAllWorktreePaths();
            var groups = DeriveWorktreeGroups(allPaths);
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
                    // Conservative: skip the group rather than risk flagging a
                    // registered dir as stranded. But COUNT the skip + log it
                    // (task 248cc2ce): a skipped group makes the scan PARTIAL, and
                    // the caller must be able to tell "found none" from "couldn't
                    // look" — otherwise an empty result is a non-falsifiable zero.
                    result.SkippedGroups++;
                    Debug.WriteLine($"[WorktreeJanitor] ScanStrandedDirsAsync skipped group {group.RepoRoot}: {ex.Message}");
                    continue;
                }

                // Materialize children with the scan's OWN guard (task 248cc2ce,
                // adversary run 2). The shared EnumerateChildren swallows enumeration
                // failures into an empty list — fine for Pass 3 (it has outer guards
                // and doesn't report completeness) but WRONG here: a swallowed failure
                // would be indistinguishable from a genuinely empty parent, letting a
                // failed scan read as status="ok"/count=0. Count the failure as a
                // skipped group instead so the result degrades to 'partial'.
                List<string> children;
                try
                {
                    children = new List<string>(Directory.EnumerateDirectories(group.ParentDir));
                }
                catch (Exception ex)
                {
                    result.SkippedGroups++;
                    Debug.WriteLine($"[WorktreeJanitor] ScanStrandedDirsAsync child-enum failed for {group.ParentDir}: {ex.Message}");
                    continue;
                }

                foreach (var child in children)
                {
                    if (registered.Contains(NormalizePath(child))) continue;

                    // Tri-state emptiness probe (task 248cc2ce, adversary run 3).
                    // The bare IsDirectoryEmpty swallows probe exceptions and returns
                    // false, which would silently drop a real (de-registered, empty)
                    // strand whose probe throws — leaving status="ok"/count=0. Here a
                    // probe FAILURE degrades the scan to 'partial' (same contract as a
                    // skipped group) so "ok" stays authoritative.
                    if (!TryIsDirectoryEmpty(child, out bool isEmpty))
                    {
                        result.SkippedGroups++;
                        Debug.WriteLine($"[WorktreeJanitor] ScanStrandedDirsAsync empty-probe failed for {child}");
                        continue;
                    }
                    if (!isEmpty) continue;
                    result.Dirs.Add(child);
                }
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
                // Materialize EAGERLY (task 248cc2ce). Directory.EnumerateDirectories
                // is lazy: a deferred MoveNext() inside the caller's foreach can throw
                // IOException/DirectoryNotFoundException if a child is deleted mid-walk
                // (e.g. Pass 3's own Directory.Delete, or a concurrent ScanStrandedDirsAsync).
                // Forcing the read here means that throw is caught by this try and yields
                // an empty set, instead of escaping into a caller that may lack an outer
                // guard (the read-only scan did).
                return new List<string>(Directory.EnumerateDirectories(parentDir));
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

        /// <summary>
        /// Tri-state emptiness probe (task 248cc2ce): returns <c>true</c> when the
        /// probe succeeded (with the answer in <paramref name="isEmpty"/>) and
        /// <c>false</c> when the probe itself failed (transient IO/access/race).
        /// Unlike <see cref="IsDirectoryEmpty"/> — whose swallow-to-<c>false</c> is
        /// the safe choice for Pass 3's DELETE (a probe failure means "don't
        /// delete") — the read-only scan must NOT treat a probe failure as
        /// "non-empty", or it would silently drop a real strand from the count
        /// while reporting <c>status="ok"</c>. The scan uses this to degrade to
        /// <c>partial</c> on probe failure instead.
        /// </summary>
        private static bool TryIsDirectoryEmpty(string dir, out bool isEmpty)
        {
            try
            {
                using var en = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
                isEmpty = !en.MoveNext();
                return true;
            }
            catch
            {
                isEmpty = false;
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

        /// <summary>Pass 2 — count of pending merges recovered (merged + branch deleted) this sweep (task d75d7d6e).</summary>
        public int MergesRecovered { get; set; }

        /// <summary>Cycle-3 — count of deferred prunes successfully retried this sweep.</summary>
        public int DeferredPrunesCompleted { get; set; }

        /// <summary>
        /// Task 248cc2ce — point-in-time count of empty, de-registered-but-on-disk
        /// worktree dirs that Pass 3 found but could NOT rmdir this sweep (a handle
        /// is still held — commonly a subagent cwd or AV). These are strands that
        /// are not self-healing this round; a non-zero value persisting across
        /// sweeps is the observable signal that something is wedged.
        /// </summary>
        public int StrandedDirsRemaining { get; set; }

        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// Result of <see cref="WorktreeJanitorService.ScanStrandedDirsAsync"/> (task
    /// 248cc2ce). A POINT-IN-TIME, read-only view distinct from
    /// <see cref="JanitorResult.StrandedDirsRemaining"/> (which is a per-sweep
    /// count of dirs Pass 3 tried and failed to rmdir). <see cref="Dirs"/> is the
    /// set of empty, de-registered-but-on-disk worktree dirs currently visible.
    /// <see cref="SkippedGroups"/> counts repo groups skipped because EITHER their
    /// <c>git worktree list</c> OR their child-directory enumeration failed; when
    /// &gt; 0 the scan is <see cref="Complete"/> ==
    /// false (PARTIAL), so a caller must NOT read an empty <see cref="Dirs"/> as a
    /// trustworthy "no strands" — the whole falsifiability point is to never
    /// conflate "found none" with "couldn't look".
    /// </summary>
    public class StrandedScanResult
    {
        public List<string> Dirs { get; } = new List<string>();

        public int SkippedGroups { get; set; }

        public bool Complete => SkippedGroups == 0;
    }
}
