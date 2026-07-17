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
    /// <para>Nearly stateless — the only cross-sweep state is
    /// <see cref="_lastSweepActivityKeys"/>, a dedup memory that suppresses activity
    /// lines identical to the previous sweep's so a persistent wedge logs once, not
    /// every 5 min (task 7d140c8b). Decoupled from <see cref="MessageBroker"/> via two
    /// callbacks: <see cref="JanitorActivityCallback"/> for activity logging
    /// and a <see cref="Func{T,TResult}"/> for resolving project paths from
    /// task ids. The wire-up site (typically <c>MainForm</c>) provides both.</para>
    /// </summary>
    public class WorktreeJanitorService
    {
        private readonly TaskDatabase _db;

        // Cross-sweep dedup memory (task 7d140c8b): the (action|relatedId|content)
        // keys recorded on the PREVIOUS sweep. A persistently-notable condition — e.g.
        // a done-task branch that keeps failing to auto-merge (task d14048ef) — yields
        // the SAME activity line every 5-min sweep; without this the janitor spammed the
        // HUD activity feed once per sweep forever. We log such a condition ONCE and stay
        // quiet until its content changes or it clears. Accessed only inside SweepCoreAsync,
        // which the re-entrancy gate below serializes — so no lock is needed on this field.
        private HashSet<string> _lastSweepActivityKeys = new HashSet<string>(StringComparer.Ordinal);

        // Re-entrancy gate (codex adversary, task 7d140c8b): System.Threading.Timer does NOT
        // serialize its callbacks, so a slow sweep (e.g. a long git merge) can be overlapped
        // by the next 5-min tick. Running two sweeps concurrently would break the dedup
        // compare→record→swap transaction (duplicate emission + stale-state overwrite). The
        // public SweepAsync skips a tick whenever a sweep is already running, so SweepCoreAsync
        // runs single-threaded and the whole transaction is atomic by construction.
        private readonly object _sweepGate = new object();
        private bool _sweepRunning;

        public WorktreeJanitorService(TaskDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Activity-log callback signature. Caller wires this to
        /// <c>ActivityService.RecordActivity</c> (or equivalent) without the
        /// janitor needing to depend on the broker / MCPServer namespace.
        /// Returns <c>true</c> when the activity was durably recorded and <c>false</c> when
        /// the write failed — the janitor's dedup only remembers delivered lines so a
        /// silently-failed write is retried next sweep rather than suppressed forever.
        /// </summary>
        public delegate bool JanitorActivityCallback(string action, string content, string relatedId);

        /// <summary>
        /// Public entry point. Serializes sweeps: if a sweep is already running (the 5-min
        /// timer can overlap a slow one — System.Threading.Timer callbacks aren't serialized),
        /// this tick is skipped and returns an empty result rather than running concurrently.
        /// That keeps the cross-sweep dedup transaction in <see cref="SweepCoreAsync"/> atomic.
        /// </summary>
        public async Task<JanitorResult> SweepAsync(
            Func<string, string> getProjectPathForTask,
            JanitorActivityCallback recordActivity,
            Func<string, Task<bool>> tryDeferredPruneRetry = null,
            Func<string, string, Task<MergeResult>> tryMergeForTask = null)
        {
            lock (_sweepGate)
            {
                if (_sweepRunning) return new JanitorResult(); // a sweep is in progress — skip this overlapping tick
                _sweepRunning = true;
            }
            try
            {
                return await SweepCoreAsync(getProjectPathForTask, recordActivity, tryDeferredPruneRetry, tryMergeForTask).ConfigureAwait(false);
            }
            finally
            {
                lock (_sweepGate) { _sweepRunning = false; }
            }
        }

        /// <summary>
        /// Run a single sweep. Safe to call from any thread; performs only
        /// small read+update DB operations. Logs notable events via
        /// <paramref name="recordActivity"/>; logs nothing when the sweep is a
        /// pure no-op (no missing dirs, no pending merges). Invoked only through
        /// <see cref="SweepAsync"/>, which guarantees one sweep runs at a time.
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
        private async Task<JanitorResult> SweepCoreAsync(
            Func<string, string> getProjectPathForTask,
            JanitorActivityCallback recordActivity,
            Func<string, Task<bool>> tryDeferredPruneRetry = null,
            Func<string, string, Task<MergeResult>> tryMergeForTask = null)
        {
            var result = new JanitorResult();

            // Source-side dedup (task 7d140c8b): route every activity record through a
            // filter that suppresses lines identical to one recorded on the PREVIOUS sweep,
            // so a persistent wedge (e.g. an unmergeable orphaned branch) logs once, not
            // every 5 min. Reassigning the parameter here routes ALL existing
            // recordActivity?.Invoke(...) sites below — per-pass lines AND the aggregate
            // summary — through the filter with no other edits. currentSweepKeys captures
            // everything seen THIS sweep (recorded or suppressed) so a still-present
            // condition stays suppressed next sweep too; a cleared condition drops out and
            // re-logs if it ever recurs.
            var rawRecord = recordActivity;
            var currentSweepKeys = new HashSet<string>(StringComparer.Ordinal);
            recordActivity = (action, content, relatedId) =>
            {
                // Join with a control char (U+0001) that cannot occur in activity text,
                // so distinct (action, relatedId, content) triples can never collide.
                var key = string.Join((char)1, action ?? string.Empty, relatedId ?? string.Empty, content ?? string.Empty);
                if (_lastSweepActivityKeys.Contains(key))
                {
                    currentSweepKeys.Add(key); // still present — keep it remembered so it stays suppressed
                    return true;
                }
                // New/changed this sweep: emit, and only REMEMBER the key once delivery is
                // confirmed. If the write silently failed (rawRecord returns false), we do NOT
                // dedup it — the identical actionable condition is retried next sweep instead of
                // being suppressed forever (codex adversary HIGH, task 7d140c8b). A null sink
                // (headless/tests) counts as delivered so those runs don't loop.
                bool delivered = rawRecord?.Invoke(action, content, relatedId) ?? true;
                if (delivered) currentSweepKeys.Add(key);
                return delivered;
            };

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
                            // Hoisted out of the if-block so the pending-merge path
                            // below can surface retry.Stderr (the refusal reason).
                            MergeResult retry = null;
                            if (tryMergeForTask != null)
                            {
                                try
                                {
                                    retry = await tryMergeForTask(record.TaskId, projectPath).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    result.Errors.Add($"Pass 2 merge-retry {record.TaskId}: {ex.Message}");
                                }

                                if (retry != null && retry.Merged)
                                {
                                    // A REAL merge landed the branch in trunk. On the
                                    // partial path (merge succeeded, branch -d failed)
                                    // Stderr is set and the branch is still alive — don't
                                    // claim "and deleted" (task 90c2acc6, Codex security
                                    // MEDIUM, mirroring the broker fix). The next sweep
                                    // re-detects the leftover branch and cleans it up as a
                                    // benign skip.
                                    result.MergesRecovered++;
                                    bool cleanupPending = !string.IsNullOrWhiteSpace(retry.Stderr);
                                    string shortId = record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length));
                                    recordActivity?.Invoke(
                                        "janitor_merge_recovered",
                                        cleanupPending
                                            ? $"Auto-merge recovered for done task {shortId}: branch {record.BranchName} merged into {retry.MergedInto ?? "trunk"}; branch cleanup pending (will be removed on the next sweep)."
                                            : $"Auto-merge recovered for done task {shortId}: branch {record.BranchName} merged into {retry.MergedInto ?? "trunk"} and deleted.",
                                        record.TaskId);
                                    continue;
                                }

                                // Success WITHOUT a merge (task 90c2acc6, Suspect A):
                                // a benign skip — no commits to merge, or the branch
                                // resolved itself (already deleted). Nothing landed,
                                // but nothing is pending either: do NOT flag it as a
                                // pending merge AND do NOT count it as a recovery. The
                                // old code conflated this with a real merge via
                                // .Success, reporting "merged into trunk" for a no-op.
                                if (retry != null && retry.Success)
                                {
                                    continue;
                                }
                            }

                            // Mutating-timeout hazard (Eval P5 pipeline Run 2): a
                            // timed-out merge is retry-later with an UNKNOWN outcome —
                            // NOT an ordinary "branch still alive, merge it" pending
                            // merge. And if the follow-up abort also failed, the checkout
                            // may be HALF-MERGED. Surface both cases loudly and
                            // distinctly rather than folding them into the generic
                            // pending-merge line (or, worse, proceeding silently).
                            if (retry != null && retry.TimedOut)
                            {
                                string tShort = record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length));
                                result.PendingMerges.Add(record.TaskId);
                                recordActivity?.Invoke(
                                    retry.IndeterminateState ? "janitor_merge_indeterminate" : "janitor_merge_timeout",
                                    retry.IndeterminateState
                                        ? $"HALF-MERGED checkout — MANUAL CLEANUP NEEDED for done task {tShort}: {retry.Stderr}"
                                        : $"Auto-merge for done task {tShort} timed out and was rolled back; will retry next sweep. {retry.Stderr}",
                                    record.TaskId);
                                continue;
                            }

                            // Surface the actual refusal/failure reason from the retry
                            // (task 90c2acc6, Codex adversary MEDIUM): the wrong-branch
                            // and fail-closed refusals carry an actionable Stderr (e.g.
                            // "main checkout is on 'feature/x', expected 'master'"). The
                            // old generic message discarded it, so operators got the same
                            // unhelpful line every sweep with no clue to the real cause.
                            result.PendingMerges.Add(record.TaskId);
                            string failReason = retry != null && !string.IsNullOrWhiteSpace(retry.Stderr)
                                ? $" Reason: {retry.Stderr.Trim()}"
                                : "";
                            recordActivity?.Invoke(
                                "janitor_pending_merge",
                                $"Branch {record.BranchName} still alive for done task {record.TaskId.Substring(0, Math.Min(8, record.TaskId.Length))} — merge it manually or re-mark the task done to retry auto-merge.{failReason}",
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
                var groups = DeriveWorktreeGroups(allPaths, out var underivableParents);

                // Task e85eba13: parents dropped by derivation are logged (not
                // fed to result.Errors — a dead repo's leftover rows would make
                // the sweep summary chronically noisy). The on-demand stranded
                // scan is the completeness reporter; it counts these as skips.
                foreach (var parent in underivableParents)
                {
                    Debug.WriteLine($"[WorktreeJanitor] Pass 3: repo root underivable for worktree parent {parent} — parent not scanned this sweep");
                }

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

                            // Child-shape gate (e85eba13, flagged independently by
                            // BOTH codex gates): the janitor owns only MT-shaped
                            // worktree dirs (8-hex id, optionally id--slug). Now
                            // that the modern .claude/worktrees parent is finally
                            // scanned, an empty non-MT dir placed there (a user
                            // folder, or a Claude-Code-created "agent-*" worktree
                            // husk) must NOT be swept up — the registered/.git
                            // guards validate the PARENT, not each child's
                            // ownership. Non-MT husks are their creator's to clean.
                            if (!WorktreeLayout.IsWorktreeIdSegment(Path.GetFileName(child))) continue;
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
                // Actionable summaries (caught errors, or rmdir-blocked strands that need a
                // human to release the holding process) use a distinct action so the HUD tiers
                // them as IMPORTANT and they survive the Important filter — a plain
                // "janitor_sweep" is housekeeping and would be hidden otherwise (codex adversary,
                // task 7d140c8b). Routing importance via the action name (not display-text
                // parsing) keeps the dashboard classifier simple and robust.
                bool actionable = result.Errors.Count > 0 || result.StrandedDirsRemaining > 0;
                var summary = $"Worktree janitor: reconciled {result.ReconciledMissing} missing, {result.MergesRecovered} merges recovered, {result.PendingMerges.Count} pending merge, {result.OrphansRemoved} orphans removed, {result.StrandedDirsRemaining} rmdir-blocked this sweep (held — not a live total; query api/worktrees/stranded for that), {result.DeferredPrunesCompleted} deferred prunes completed, {result.Errors.Count} errors.";

                // Append the actual error text to the actionable summary. The base summary carries
                // only COUNTS, so two sweeps with the same error COUNT but DIFFERENT errors would
                // otherwise produce identical dedup keys and the second (new) error would be
                // suppressed (codex, task 7d140c8b). The summary content doubles as the cross-sweep
                // dedup key, so we must NOT let display truncation also truncate the dedup identity:
                // a bounded 300-char PREVIEW is shown, but a stable FNV-1a digest of the FULL ordered
                // error set is appended so the key reflects the complete identity — two error sets that
                // merely share the first 300 chars (e.g. long worktree paths with a common prefix) still
                // get distinct keys. Identical persistent error sets still dedup. (StrandedDirsRemaining
                // always pairs with an entry in Errors, so this also distinguishes stranded paths.)
                if (actionable && result.Errors.Count > 0)
                {
                    var joinedFull = string.Join(" | ", result.Errors);
                    var preview = joinedFull.Length > 300 ? joinedFull.Substring(0, 300) + "…" : joinedFull;
                    uint sig = 2166136261u; // FNV-1a over the FULL text — deterministic, not display-truncated
                    foreach (char c in joinedFull) { sig = (sig ^ c) * 16777619u; }
                    summary += $" Errors: {preview} [sig {sig:x8}]";
                }

                recordActivity(
                    actionable ? "janitor_sweep_attention" : "janitor_sweep",
                    summary,
                    null);
            }

            // Remember every activity key seen this sweep (recorded OR suppressed) so an
            // unchanged condition stays quiet next sweep, while a cleared one drops out and
            // re-logs if it recurs. Single-threaded (serialized by the SweepAsync gate).
            _lastSweepActivityKeys = currentSweepKeys;

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
            var groups = DeriveWorktreeGroups(allPaths, out var underivableParents);
            AppendLegacyParentsFromActiveRepos(groups);

            // Task e85eba13: a parent whose repo root can't be derived (unknown
            // layout, or .git gone at the candidate) is a couldn't-look — count
            // it as a skipped group so the scan degrades to 'partial' instead of
            // a silent authoritative-looking ok/0. Only when the parent still
            // exists on disk: a parent of a fully deleted repo has nothing left
            // to scan, and counting it would make every scan chronically partial
            // on old DB rows. The parent dir (not a repo root — deriving one is
            // exactly what failed) goes into SkippedGroupRepoRoots so per-project
            // attribution can containment-match it.
            foreach (var parent in underivableParents)
            {
                bool exists;
                try { exists = Directory.Exists(parent); }
                catch { exists = true; } // probe failure: assume present — report, don't hide
                if (!exists) continue;
                result.SkippedGroups++;
                result.SkippedGroupRepoRoots.Add(parent);
                Debug.WriteLine($"[WorktreeJanitor] ScanStrandedDirsAsync: repo root underivable for existing worktree parent {parent} — counted as skipped group");
            }

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
                    result.SkippedGroupRepoRoots.Add(group.RepoRoot);
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
                    result.SkippedGroupRepoRoots.Add(group.RepoRoot);
                    Debug.WriteLine($"[WorktreeJanitor] ScanStrandedDirsAsync child-enum failed for {group.ParentDir}: {ex.Message}");
                    continue;
                }

                foreach (var child in children)
                {
                    if (registered.Contains(NormalizePath(child))) continue;

                    // Child-shape gate (e85eba13): mirror Pass 3 — report only
                    // MT-shaped ids, so the API never advertises (and session-start
                    // never nags about) husks the janitor will refuse to remove.
                    if (!WorktreeLayout.IsWorktreeIdSegment(Path.GetFileName(child))) continue;

                    // Tri-state emptiness probe (task 248cc2ce, adversary run 3).
                    // The bare IsDirectoryEmpty swallows probe exceptions and returns
                    // false, which would silently drop a real (de-registered, empty)
                    // strand whose probe throws — leaving status="ok"/count=0. Here a
                    // probe FAILURE degrades the scan to 'partial' (same contract as a
                    // skipped group) so "ok" stays authoritative.
                    if (!TryIsDirectoryEmpty(child, out bool isEmpty))
                    {
                        result.SkippedGroups++;
                        result.SkippedGroupRepoRoots.Add(group.RepoRoot);
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
        /// Read-only scan (task 94356803): enumerate the pruned-worktree rows whose
        /// owning task is done and whose <c>task/{id}</c> branch STILL exists in git
        /// — i.e. the pending-merge findings Pass 2 flags to the activity feed — and
        /// return them WITHOUT attempting any merge. Backs the on-demand
        /// pending-merge surface (REST + session-start) so an unresolved finding is
        /// visible to the next agent booting on the project, not just to whoever
        /// happened to read the feed. Same falsifiability contract as
        /// <see cref="ScanStrandedDirsAsync"/>: a record that could not be checked
        /// (unresolvable project, git failure/timeout) increments
        /// <see cref="PendingMergeScanResult.SkippedRecords"/> and degrades the scan
        /// to partial — "found none" is never conflated with "couldn't look".
        /// </summary>
        /// <param name="getProjectPathForTask">Resolver: taskId → repo root, or null
        /// when the task's project can't be located (counted as a skipped record).</param>
        public async Task<PendingMergeScanResult> ScanPendingMergesAsync(Func<string, string> getProjectPathForTask)
        {
            var result = new PendingMergeScanResult();

            List<MCPServer.Models.TaskWorktree> prunedDone;
            try
            {
                prunedDone = _db.ListPrunedWorktreesForDoneTasks();
            }
            catch (Exception ex)
            {
                // Couldn't even enumerate — the scan is partial-with-nothing, not "ok, none".
                result.SkippedRecords++;
                Debug.WriteLine($"[WorktreeJanitor] ScanPendingMergesAsync list failed: {ex.Message}");
                return result;
            }

            // A task can have multiple worktree rows (per-agent); report each live
            // (taskId, branch) pair once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in prunedDone)
            {
                try
                {
                    string projectPath = getProjectPathForTask?.Invoke(record.TaskId);
                    if (string.IsNullOrEmpty(projectPath))
                    {
                        result.SkippedRecords++;
                        result.SkippedTaskIds.Add(record.TaskId);
                        continue;
                    }

                    if (!seen.Add(record.TaskId + "|" + record.BranchName)) continue;

                    bool branchExists = await BranchExistsAsync(projectPath, record.BranchName).ConfigureAwait(false);
                    if (branchExists)
                    {
                        result.Items.Add(new PendingMergeInfo
                        {
                            TaskId = record.TaskId,
                            BranchName = record.BranchName,
                            RepoRoot = projectPath,
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Timeout or git failure — retry-later territory, count as skipped.
                    result.SkippedRecords++;
                    result.SkippedTaskIds.Add(record.TaskId);
                    Debug.WriteLine($"[WorktreeJanitor] ScanPendingMergesAsync check failed for {record.TaskId}: {ex.Message}");
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
            => DeriveWorktreeGroups(worktreePaths, out _);

        /// <summary>
        /// Overload with dropped-parent accounting (task e85eba13). A parent dir
        /// whose repo-root derivation fails is a "couldn't look", not a "found
        /// none": before this overload existed, such parents silently vanished
        /// here — BEFORE any group was formed — so neither Pass 3 nor
        /// <see cref="ScanStrandedDirsAsync"/> ever visited them, no skip counter
        /// fired, and the stranded scan reported an authoritative-looking
        /// ok/0/0 while real strands sat on disk (that is exactly how the
        /// <c>.claude/worktrees</c> layout blindness stayed invisible). Callers
        /// that report scan completeness MUST surface
        /// <paramref name="underivableParents"/>; kept side-effect-free (no
        /// <c>Directory.Exists</c> probe here) so it stays a pure, testable
        /// derivation — callers decide whether a missing-on-disk parent is worth
        /// reporting.
        /// </summary>
        internal static List<WorktreeGroup> DeriveWorktreeGroups(IEnumerable<string> worktreePaths, out List<string> underivableParents)
        {
            var groups = new Dictionary<string, WorktreeGroup>(StringComparer.OrdinalIgnoreCase);
            var dropped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            underivableParents = new List<string>();
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

                string key = NormalizePath(parentDir);
                if (groups.ContainsKey(key) || dropped.ContainsKey(key)) continue;

                string repoRoot = WorktreeLayout.DeriveRepoRootFromParent(parentDir);
                if (string.IsNullOrEmpty(repoRoot))
                {
                    dropped[key] = parentDir;
                    continue;
                }

                groups[key] = new WorktreeGroup { ParentDir = parentDir, RepoRoot = repoRoot };
            }

            underivableParents.AddRange(dropped.Values);
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
            var result = await GitExec.RunAsync(repoRoot, "worktree", "list", "--porcelain").ConfigureAwait(false);
            // A timed-out `git worktree list` yields NO reliable registered-path
            // set. Treating that empty set as authoritative would make Pass 3 see
            // every on-disk worktree as unregistered (stranded) and rmdir it. Throw
            // a distinct TimeoutException so the caller's conservative catch skips
            // this group and retries next sweep — timeout is retry-later, NOT
            // stranded-worktree evidence.
            if (result.TimedOut)
            {
                throw new TimeoutException($"git worktree list timed out for {repoRoot} — retry next sweep");
            }
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"git worktree list exit {result.ExitCode}: {result.Stderr.Trim()}");
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in result.Stdout.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
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
            var result = await GitExec.RunAsync(repoRoot, "branch", "--list", branchName).ConfigureAwait(false);
            // A timed-out branch check is NOT a "branch is gone" signal — returning
            // false would let Pass 2 mishandle a still-alive branch. Throw so the
            // per-record catch skips this record and retries next sweep.
            if (result.TimedOut)
            {
                throw new TimeoutException($"git branch --list timed out for {branchName} — retry next sweep");
            }
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout);
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

        /// <summary>
        /// Repo roots of the skipped groups (pipeline Run-1 debugger MEDIUM): every
        /// skip in this scan belongs to a concrete repo group, so a per-project
        /// consumer can attribute partiality to its own repo instead of inheriting
        /// every other repo's failures.
        /// </summary>
        public List<string> SkippedGroupRepoRoots { get; } = new List<string>();

        public bool Complete => SkippedGroups == 0;
    }

    /// <summary>
    /// One live pending-merge finding from
    /// <see cref="WorktreeJanitorService.ScanPendingMergesAsync"/>: a done task
    /// whose <see cref="BranchName"/> still exists in git at <see cref="RepoRoot"/>.
    /// </summary>
    public class PendingMergeInfo
    {
        public string TaskId { get; set; }

        public string BranchName { get; set; }

        public string RepoRoot { get; set; }
    }

    /// <summary>
    /// Result of <see cref="WorktreeJanitorService.ScanPendingMergesAsync"/> (task
    /// 94356803). Same falsifiability contract as <see cref="StrandedScanResult"/>:
    /// <see cref="SkippedRecords"/> counts rows that could not be checked
    /// (unresolvable project, git failure/timeout, or a failed enumeration); when
    /// &gt; 0 the scan is <see cref="Complete"/> == false (PARTIAL) and an empty
    /// <see cref="Items"/> must not be read as a trustworthy "no pending merges".
    /// </summary>
    public class PendingMergeScanResult
    {
        public List<PendingMergeInfo> Items { get; } = new List<PendingMergeInfo>();

        public int SkippedRecords { get; set; }

        /// <summary>
        /// Task ids of the skipped records (pipeline Run-1 debugger MEDIUM: lets a
        /// per-project consumer attribute partiality to ITS project instead of
        /// inheriting every other project's failures). A skipped enumeration (the
        /// whole listing failed) has no task id — that skip is attributable to no
        /// one and every project must treat the scan as partial.
        /// </summary>
        public List<string> SkippedTaskIds { get; } = new List<string>();

        public bool Complete => SkippedRecords == 0;
    }
}
