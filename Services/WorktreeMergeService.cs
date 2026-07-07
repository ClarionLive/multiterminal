using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Phase 3 of per-task worktree isolation (task 2b98098e): merges the
    /// task branch <c>task/{taskIdShort}</c> into the main checkout's current
    /// trunk and deletes the merged branch. Conflicts trigger an immediate
    /// <c>merge --abort</c> so the main checkout is never left half-merged.
    ///
    /// <para>Stateless service. Wraps <c>git merge / merge --abort / branch -d
    /// / log --oneline / rev-parse --abbrev-ref / branch --list / status --porcelain</c>
    /// via <see cref="Process"/>. Returns structured <see cref="MergeResult"/>
    /// instead of throwing on git failure, so the lifecycle hook in
    /// <c>MessageBroker.UpdateTaskStatus</c> can branch on the outcome.</para>
    ///
    /// <para>Order in the lifecycle pipeline: Phase 2 commits → Phase 1 prunes
    /// the worktree (releases the branch lock) → THIS service merges and
    /// deletes the branch. The merge MUST run after prune because git refuses
    /// to merge a branch while it's checked out anywhere.</para>
    /// </summary>
    public class WorktreeMergeService
    {
        private readonly TaskDatabase _db;

        /// <summary>
        /// Repo-root-relative paths (forward-slash) that MT writes into the
        /// working tree as part of its own bookkeeping — NOT user work. When
        /// the ONLY dirty tracked files at merge time are in this allowlist,
        /// the merge auto-commits them as a <c>chore:</c> commit instead of
        /// refusing (task d75d7d6e): MT rewrites <c>.claude/project.json</c>
        /// during the very task-done flow that triggers the merge, so without
        /// this carve-out every auto-merge was blocked by MT's own churn.
        /// Any dirty tracked file OUTSIDE this list still blocks the merge —
        /// genuine user work is never silently committed.
        /// </summary>
        private static readonly string[] BookkeepingPaths = { ".claude/project.json" };

        public WorktreeMergeService(TaskDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Merges the task's branch into the main checkout's current trunk.
        /// Idempotent re: branch state — if the branch is already gone or has
        /// no commits beyond trunk, returns Success with a SkippedReason.
        ///
        /// <para>Decisions ratified during planning: merge strategy is plain
        /// <c>--no-edit</c> (allows fast-forwards AND merge commits); trunk is
        /// detected at merge time via the main checkout's current branch (cope
        /// with main / master / custom); main-checkout-dirty refuses with a
        /// clear "commit or stash" message; conflict aborts cleanly and leaves
        /// the task branch alive for manual resolution.</para>
        /// </summary>
        public async Task<MergeResult> MergeForTaskAsync(string taskId, string repoRoot, string expectedTrunk = null)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            var record = _db.GetWorktreeForTask(taskId);
            if (record == null)
            {
                return new MergeResult
                {
                    Success = true,
                    SkippedReason = "no worktree record"
                };
            }

            // Phase 3 always merges the CANONICAL task branch into trunk, derived by
            // name (not from the record, which can resolve to a helper row when the
            // assignee never materialized a canonical worktree). In the normal case
            // this equals the canonical record's branch.
            string branchName = WorktreeNaming.CanonicalBranch(taskId);

            var trunkResult = await GitExec.RunAsync(repoRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
            if (trunkResult.ExitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"rev-parse trunk failed: {trunkResult.Stderr.Trim()}"
                };
            }

            string trunk = trunkResult.Stdout.Trim();
            if (string.Equals(trunk, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = "Main checkout has detached HEAD; cannot merge."
                };
            }

            if (string.Equals(trunk, branchName, StringComparison.Ordinal))
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"Main checkout is on the task's own branch ({trunk}); refusing to merge into self."
                };
            }

            // Suspect B (task 90c2acc6): the merge targets whatever branch the main
            // checkout currently has checked out. If that's NOT the intended trunk
            // (someone parked the main checkout on a feature branch, or a prior
            // operation left it elsewhere), the task branch would silently land in
            // the wrong branch and never reach the default branch. Resolve the
            // EXPECTED trunk and refuse rather than merge blind.
            //
            // Resolution order: (1) the caller-supplied expectedTrunk (the project's
            // configured git_default_branch — authoritative); (2) the remote's
            // published default (origin/HEAD); (3) the SOLE non-task local branch
            // when exactly one exists (covers custom trunk names like 'develop').
            //
            // FAIL CLOSED (pipeline run 1, Codex security + adversary HIGH): if NONE
            // of these resolve — e.g. a repo carrying BOTH a stale 'main' and the
            // working 'master', or a non-conventional trunk name with no configured
            // git_default_branch — we must NOT fall back to "merge into whatever HEAD
            // is on". That fail-open path reopened the exact silent wrong-branch merge
            // this guard exists to stop. Instead we refuse and tell the operator to
            // set the project's git_default_branch. A destructive-ish merge never
            // proceeds on a guessed trunk.
            string wantTrunk = string.IsNullOrWhiteSpace(expectedTrunk)
                ? await DetectDefaultBranchAsync(repoRoot).ConfigureAwait(false)
                : expectedTrunk.Trim();
            if (string.IsNullOrWhiteSpace(wantTrunk))
            {
                return new MergeResult
                {
                    Success = false,
                    Merged = false,
                    MergedInto = trunk,
                    Stderr = $"Could not determine the repository's default branch (no project git_default_branch configured, no origin/HEAD, and more than one non-task branch exists so the trunk is ambiguous). Refusing to auto-merge '{branchName}' to avoid landing it in the wrong branch (main checkout currently on '{trunk}'). Set the project's default branch (git_default_branch) and re-mark the task done to retry."
                };
            }
            if (!string.Equals(trunk, wantTrunk, StringComparison.Ordinal))
            {
                return new MergeResult
                {
                    Success = false,
                    Merged = false,
                    MergedInto = trunk,
                    Stderr = $"Main checkout is on '{trunk}', but the expected trunk is '{wantTrunk}'. Refusing to merge '{branchName}' into the wrong branch — check out '{wantTrunk}' in the main checkout and re-mark the task done to retry."
                };
            }

            var branchListResult = await GitExec.RunAsync(repoRoot, "branch", "--list", branchName).ConfigureAwait(false);
            if (branchListResult.ExitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"branch --list failed: {branchListResult.Stderr.Trim()}"
                };
            }
            if (string.IsNullOrWhiteSpace(branchListResult.Stdout))
            {
                return new MergeResult
                {
                    Success = true,
                    SkippedReason = $"task branch '{branchName}' already deleted"
                };
            }

            var aheadResult = await GitExec.RunAsync(
                repoRoot, "log", "--oneline", $"{trunk}..{branchName}").ConfigureAwait(false);
            if (aheadResult.ExitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"git log {trunk}..{branchName} failed: {aheadResult.Stderr.Trim()}"
                };
            }
            if (string.IsNullOrWhiteSpace(aheadResult.Stdout))
            {
                // Task branch has no commits beyond trunk — safe to delete without merging.
                var deleteEmpty = await GitExec.RunAsync(repoRoot, "branch", "-d", branchName).ConfigureAwait(false);
                if (deleteEmpty.ExitCode != 0)
                {
                    return new MergeResult
                    {
                        Success = false,
                        Stderr = $"branch -d (empty) failed: {deleteEmpty.Stderr.Trim()}"
                    };
                }
                return new MergeResult
                {
                    Success = true,
                    MergedInto = trunk,
                    SkippedReason = $"no commits to merge; branch '{branchName}' deleted"
                };
            }

            // Refuse to merge if the main checkout has TRACKED uncommitted changes —
            // git would refuse anyway, but our message is clearer. Use
            // --untracked-files=no because untracked files don't block merges
            // unless they'd be overwritten, and git's own check catches that case.
            var dirtyCheck = await GitExec.RunAsync(repoRoot, "status", "--porcelain", "--untracked-files=no").ConfigureAwait(false);
            if (dirtyCheck.ExitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"main-checkout status check failed: {dirtyCheck.Stderr.Trim()}"
                };
            }
            if (!string.IsNullOrWhiteSpace(dirtyCheck.Stdout))
            {
                var dirtyPaths = ParsePorcelainPaths(dirtyCheck.Stdout);
                var nonBookkeeping = new System.Collections.Generic.List<string>();
                var bookkeeping = new System.Collections.Generic.List<string>();
                foreach (var p in dirtyPaths)
                {
                    if (IsBookkeepingPath(p)) bookkeeping.Add(p);
                    else nonBookkeeping.Add(p);
                }

                if (nonBookkeeping.Count > 0)
                {
                    // Genuine user work is dirty — refuse, as before. Never
                    // auto-commit files outside the bookkeeping allowlist.
                    return new MergeResult
                    {
                        Success = false,
                        Stderr = $"Main checkout has uncommitted changes — cannot merge {branchName} into {trunk}. Please commit or stash before closing this task. Offending files: {string.Join(", ", nonBookkeeping)}."
                    };
                }

                // Only MT's own bookkeeping files are dirty. Commit them as a
                // chore commit so the merge precondition (clean trunk) is met
                // and the changelog churn is durably persisted rather than left
                // floating as a perpetual working-tree change.
                foreach (var p in bookkeeping)
                {
                    var addResult = await GitExec.RunAsync(repoRoot, "add", "--", p).ConfigureAwait(false);
                    if (addResult.ExitCode != 0)
                    {
                        return new MergeResult
                        {
                            Success = false,
                            Stderr = $"failed to stage bookkeeping file '{p}' before merge: {addResult.Stderr.Trim()}"
                        };
                    }
                }

                var commitResult = await GitExec.RunAsync(
                    repoRoot, GitExec.SlowOpTimeoutMs, "commit", "-m", $"chore: project.json bookkeeping for {taskId.Substring(0, Math.Min(8, taskId.Length))}").ConfigureAwait(false);
                if (commitResult.ExitCode != 0)
                {
                    return new MergeResult
                    {
                        Success = false,
                        Stderr = $"failed to commit bookkeeping files before merge: {commitResult.Stderr.Trim()}; stdout: {commitResult.Stdout.Trim()}"
                    };
                }
            }

            // TOCTOU guard (pipeline run 2, Codex security HIGH): the trunk was
            // validated several awaited git calls ago. `git merge` lands in whatever
            // HEAD currently points to, so if a concurrent actor checked out a
            // different branch in the main checkout in that window, the merge would
            // silently land in the wrong branch. Re-read HEAD immediately before the
            // merge and abort if it no longer matches the validated trunk. This
            // shrinks the race window to effectively nothing; a stronger repo-wide
            // mutex is tracked as a follow-up.
            var headRecheck = await GitExec.RunAsync(repoRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
            if (headRecheck.ExitCode != 0 || !string.Equals(headRecheck.Stdout.Trim(), trunk, StringComparison.Ordinal))
            {
                return new MergeResult
                {
                    Success = false,
                    Merged = false,
                    MergedInto = trunk,
                    Stderr = $"Main checkout's branch changed from '{trunk}' to '{headRecheck.Stdout.Trim()}' during merge preparation (concurrent checkout?); aborting to avoid merging '{branchName}' into the wrong branch. Re-mark the task done to retry."
                };
            }

            // Mutating op: use the larger slow-op budget so a legitimately slow
            // merge on a big repo isn't false-killed. GIT_TERMINAL_PROMPT=0 already
            // fails credential-prompt hangs fast, so this budget is for slow-but-real
            // merges, not for tolerating hangs — the two mechanisms compose.
            var mergeRunResult = await GitExec.RunAsync(
                repoRoot, GitExec.SlowOpTimeoutMs, "merge", "--no-edit", branchName).ConfigureAwait(false);
            if (mergeRunResult.TimedOut)
            {
                // A killed merge is UNKNOWN, not a conflict — never collapse it into
                // HadConflicts. Abort to clean up any half-applied state; if abort
                // ALSO fails the checkout may be half-merged, which we surface LOUDLY
                // (IndeterminateState) instead of proceeding silently. Give the abort
                // the slow-op budget too: a 30s-timed-out abort would flip cleanedUp
                // false and raise a spurious HALF-MERGED alarm on a merely-slow abort.
                var abort = await GitExec.RunAsync(repoRoot, GitExec.SlowOpTimeoutMs, "merge", "--abort").ConfigureAwait(false);
                bool cleanedUp = abort.ExitCode == 0 && !abort.TimedOut;
                return new MergeResult
                {
                    Success = false,
                    Merged = false,
                    TimedOut = true,
                    IndeterminateState = !cleanedUp,
                    MergedInto = trunk,
                    Stderr = cleanedUp
                        ? $"git merge timed out after {GitExec.SlowOpTimeoutMs}ms and was killed (outcome UNKNOWN — not a conflict); merge --abort cleaned up the checkout. Retry the task-done flow."
                        : $"INDETERMINATE STATE: git merge timed out after {GitExec.SlowOpTimeoutMs}ms and was killed, AND merge --abort then failed (exit {abort.ExitCode}: {abort.Stderr.Trim()}). Main checkout may be left HALF-MERGED — manual cleanup required before retrying."
                };
            }
            if (mergeRunResult.ExitCode != 0)
            {
                // Conflict (or other merge error) — abort to keep main clean.
                await GitExec.RunAsync(repoRoot, "merge", "--abort").ConfigureAwait(false);
                return new MergeResult
                {
                    Success = false,
                    HadConflicts = true,
                    MergedInto = trunk,
                    Stderr = $"git merge failed (conflict or other error): {mergeRunResult.Stderr.Trim()}; stdout: {mergeRunResult.Stdout.Trim()}"
                };
            }

            // Use lowercase -d (refuses if not merged). The merge just succeeded
            // so this should always work; if it doesn't, surface as partial
            // success — the merge is durable, only the branch cleanup failed.
            var deleteBranchResult = await GitExec.RunAsync(repoRoot, "branch", "-d", branchName).ConfigureAwait(false);
            if (deleteBranchResult.ExitCode != 0)
            {
                return new MergeResult
                {
                    Success = true,
                    Merged = true,
                    MergedInto = trunk,
                    Stderr = $"merged but branch -d failed (manual cleanup needed): {deleteBranchResult.Stderr.Trim()}"
                };
            }

            return new MergeResult
            {
                Success = true,
                Merged = true,
                MergedInto = trunk
            };
        }

        /// <summary>
        /// Resolve the trunk branch (the main checkout's current branch) to root a new
        /// canonical task branch at. Returns <c>ok=false</c> with an explanatory error
        /// when HEAD is detached or on a <c>task/</c> branch — rooting the canonical
        /// branch there would silently fork it from the wrong commit (bab81a92 pipeline
        /// fix #4). Non-throwing: callers fold the error into their HelperIntegrationResult.
        /// </summary>
        private async Task<(bool ok, string trunk, string error)> ResolveTrunkAsync(string repoRoot)
        {
            var r = await GitExec.RunAsync(repoRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
            if (r.ExitCode != 0)
                return (false, null, $"git rev-parse --abbrev-ref HEAD failed: {r.Stderr.Trim()}");
            string trunk = r.Stdout.Trim();
            if (string.Equals(trunk, "HEAD", StringComparison.OrdinalIgnoreCase))
                return (false, null, "main checkout is in detached HEAD; refusing to create the canonical branch from an unknown base");
            if (trunk.StartsWith("task/", StringComparison.Ordinal))
                return (false, null, $"main checkout is on a task branch ({trunk}), not trunk; refusing to root the canonical branch there");
            return (true, trunk, null);
        }

        /// <summary>
        /// Phase 2.5 of per-agent isolation (task bab81a92): integrate each helper
        /// branch <c>task/&lt;id&gt;--&lt;slug&gt;</c> into the canonical branch
        /// <c>task/&lt;id&gt;</c> by merging INSIDE the canonical worktree (still
        /// checked out, pre-prune). Runs BEFORE prune-all + the Phase 3 trunk merge
        /// so the trunk merge of <c>task/&lt;id&gt;</c> carries every agent's work.
        ///
        /// <para>On a merge conflict the merge is aborted (canonical worktree left
        /// clean) and the branch recorded in <see cref="HelperIntegrationResult.ConflictBranches"/>;
        /// integration stops and the caller HALTS teardown so nothing is lost.
        /// Returns success with nothing integrated for single-agent tasks.</para>
        /// </summary>
        public async Task<HelperIntegrationResult> IntegrateHelperBranchesAsync(string taskId, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            var result = new HelperIntegrationResult { Success = true };

            string canonicalBranch = WorktreeNaming.CanonicalBranch(taskId);

            // Find the canonical (is_canonical) record and collect the helper branches
            // to integrate. Two sources, unioned (bab81a92 pipeline fix #3): (a) active
            // non-canonical worktree rows, and (b) every task/<id>--<slug> branch that
            // still exists in git. Source (b) recovers a helper whose worktree row was
            // already marked pruned (janitor Pass-1 reconcile / partial prune) while its
            // committed branch survives — without it that branch would never be
            // integrated, never trunk-merged, and its commits silently stranded.
            TaskWorktree canonicalRec = null;
            var helperBranches = new System.Collections.Generic.List<string>();
            var seenBranches = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var w in _db.ListWorktreesForTask(taskId))
            {
                if (w.IsCanonical)
                {
                    if (canonicalRec == null) canonicalRec = w;
                }
                else if (string.Equals(w.Status, "active", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(w.BranchName) && seenBranches.Add(w.BranchName))
                {
                    helperBranches.Add(w.BranchName);
                }
            }

            // (b) Helper branches present in git but not covered by an active row.
            string helperPrefix = canonicalBranch + "--"; // task/<idShort>--
            var branchList = await GitExec.RunAsync(
                repoRoot, "for-each-ref", "--format=%(refname:short)", $"refs/heads/{canonicalBranch}*").ConfigureAwait(false);
            if (branchList.ExitCode == 0)
            {
                foreach (var raw in branchList.Stdout.Split('\n'))
                {
                    string b = raw.Trim();
                    if (b.StartsWith(helperPrefix, StringComparison.Ordinal) && seenBranches.Add(b))
                    {
                        helperBranches.Add(b);
                    }
                }
            }

            if (helperBranches.Count == 0)
            {
                result.SkippedReason = "no helper worktrees or branches";
                return result;
            }

            // Determine the worktree that hosts the integration merges — normally the
            // assignee's canonical worktree. If it isn't on disk (e.g. the assignee
            // never activated and only helpers worked), materialize a TRANSIENT
            // worktree on the canonical branch to host integration, then remove it in
            // the finally below. CA3003: paths are app-derived (repoRoot + taskId
            // prefix), not user-supplied.
            string canonicalWorktree = canonicalRec?.WorktreePath;
#pragma warning disable CA3003
            bool canonicalOnDisk = !string.IsNullOrEmpty(canonicalWorktree) && System.IO.Directory.Exists(canonicalWorktree);
#pragma warning restore CA3003
            bool transient = false;
            string transientPath = null;

            if (!canonicalOnDisk)
            {
                // Ensure the canonical branch exists (helpers create it when they fork,
                // but be defensive); create from current HEAD if missing.
                var refCheck = await GitExec.RunAsync(repoRoot, "show-ref", "--verify", "--quiet", $"refs/heads/{canonicalBranch}").ConfigureAwait(false);
                if (refCheck.ExitCode != 0)
                {
                    // Root the canonical branch at trunk, not at whatever HEAD points to
                    // (bab81a92 pipeline fix #4): a detached/task-branch HEAD here would
                    // silently fork the canonical branch from the wrong commit.
                    var (trunkOk, trunk, trunkErr) = await ResolveTrunkAsync(repoRoot).ConfigureAwait(false);
                    if (!trunkOk)
                    {
                        result.Success = false;
                        result.Stderr = $"could not create canonical branch '{canonicalBranch}' for integration: {trunkErr}";
                        return result;
                    }
                    var branchCreate = await GitExec.RunAsync(repoRoot, "branch", canonicalBranch, trunk).ConfigureAwait(false);
                    if (branchCreate.ExitCode != 0)
                    {
                        result.Success = false;
                        result.Stderr = $"could not create canonical branch '{canonicalBranch}' from '{trunk}' for integration: {branchCreate.Stderr.Trim()}";
                        return result;
                    }
                }

                transientPath = System.IO.Path.Combine(
                    repoRoot.TrimEnd('\\', '/'), ".claude", "worktrees", WorktreeNaming.ShortId(taskId) + "__integrate");
                // Self-heal: a prior crash mid-integration can leave a stale transient
                // worktree registered, which would make the `git worktree add` below
                // fail. Best-effort remove it first (no-op when absent), then prune any
                // dangling administrative entry.
                await GitExec.RunAsync(repoRoot, "worktree", "remove", "--force", transientPath).ConfigureAwait(false);
                await GitExec.RunAsync(repoRoot, "worktree", "prune").ConfigureAwait(false);
                var add = await GitExec.RunAsync(repoRoot, GitExec.SlowOpTimeoutMs, "worktree", "add", transientPath, canonicalBranch).ConfigureAwait(false);
                if (add.ExitCode != 0)
                {
                    result.Success = false;
                    result.Stderr = $"could not create transient canonical worktree for integration: {add.Stderr.Trim()}";
                    return result;
                }
                canonicalWorktree = transientPath;
                transient = true;
            }

            try
            {
                // Confirm the host worktree is on the canonical branch before merging.
                var headResult = await GitExec.RunAsync(canonicalWorktree, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
                if (headResult.ExitCode != 0
                    || !string.Equals(headResult.Stdout.Trim(), canonicalBranch, StringComparison.Ordinal))
                {
                    result.Success = false;
                    result.Stderr = $"canonical worktree not on '{canonicalBranch}' (HEAD='{headResult.Stdout.Trim()}'); refusing to integrate";
                    return result;
                }

                foreach (var helperBranch in helperBranches)
                {
                    var ahead = await GitExec.RunAsync(
                        repoRoot, "log", "--oneline", $"{canonicalBranch}..{helperBranch}").ConfigureAwait(false);
                    if (ahead.ExitCode != 0)
                    {
                        result.Success = false;
                        result.Stderr = $"git log {canonicalBranch}..{helperBranch} failed: {ahead.Stderr.Trim()}";
                        return result;
                    }
                    if (string.IsNullOrWhiteSpace(ahead.Stdout))
                    {
                        // No commits beyond canonical — nothing to merge, but still a
                        // branch to clean up after prune.
                        result.IntegratedBranches.Add(helperBranch);
                        continue;
                    }

                    // Mutating op — larger slow-op budget (see MergeForTaskAsync).
                    var merge = await GitExec.RunAsync(canonicalWorktree, GitExec.SlowOpTimeoutMs, "merge", "--no-edit", helperBranch).ConfigureAwait(false);
                    if (merge.TimedOut)
                    {
                        // Killed merge is UNKNOWN, not a conflict. Abort to clean up;
                        // if abort also fails the canonical worktree may be half-merged
                        // — surface loudly (IndeterminateState), don't record it as a
                        // conflict. Caller halts teardown and preserves branches. Slow-op
                        // budget on the abort (see MergeForTaskAsync) to avoid a spurious
                        // HALF-MERGED alarm on a merely-slow abort.
                        var abort = await GitExec.RunAsync(canonicalWorktree, GitExec.SlowOpTimeoutMs, "merge", "--abort").ConfigureAwait(false);
                        bool cleanedUp = abort.ExitCode == 0 && !abort.TimedOut;
                        result.Success = false;
                        result.TimedOut = true;
                        result.IndeterminateState = !cleanedUp;
                        result.Stderr = cleanedUp
                            ? $"integration of '{helperBranch}' into '{canonicalBranch}' timed out after {GitExec.SlowOpTimeoutMs}ms and was killed (outcome UNKNOWN — not a conflict); aborted. Retry."
                            : $"INDETERMINATE STATE: integration of '{helperBranch}' timed out after {GitExec.SlowOpTimeoutMs}ms and was killed, AND merge --abort then failed (exit {abort.ExitCode}: {abort.Stderr.Trim()}). Canonical worktree may be HALF-MERGED — manual cleanup required.";
                        return result;
                    }
                    if (merge.ExitCode != 0)
                    {
                        // Conflict — abort to keep the canonical worktree clean, record
                        // the branch, and stop. Caller preserves all worktrees/branches.
                        await GitExec.RunAsync(canonicalWorktree, "merge", "--abort").ConfigureAwait(false);
                        result.Success = false;
                        result.HadConflicts = true;
                        result.ConflictBranches.Add(helperBranch);
                        result.Stderr = $"integration of '{helperBranch}' into '{canonicalBranch}' conflicted: {merge.Stderr.Trim()}; stdout: {merge.Stdout.Trim()}";
                        return result;
                    }

                    result.IntegratedBranches.Add(helperBranch);
                }

                return result;
            }
            finally
            {
                if (transient && !string.IsNullOrEmpty(transientPath))
                {
                    // Remove the transient host worktree; integration commits live on
                    // the canonical branch ref, so --force is safe.
                    await GitExec.RunAsync(repoRoot, "worktree", "remove", "--force", transientPath).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Deletes a local branch. <paramref name="force"/> uses <c>-D</c> (delete
        /// regardless of merge state) vs <c>-d</c> (refuse if unmerged). Returns
        /// <c>true</c> on success. Used to clean up integrated helper branches after
        /// prune-all (their commits live in the canonical branch but not yet trunk,
        /// so <c>-D</c> is required).
        /// </summary>
        public async Task<bool> DeleteBranchAsync(string repoRoot, string branchName, bool force)
        {
            if (string.IsNullOrEmpty(repoRoot) || string.IsNullOrEmpty(branchName)) return false;
            var res = await GitExec.RunAsync(repoRoot, "branch", force ? "-D" : "-d", branchName).ConfigureAwait(false);
            return res.ExitCode == 0;
        }

        /// <summary>
        /// Parses repo-root-relative paths out of <c>git status --porcelain</c>
        /// (v1) output. Each line is <c>XY PATH</c>; renames/copies are
        /// <c>XY ORIG -&gt; NEW</c> — we take NEW. Paths are normalized to
        /// forward slashes for allowlist comparison. Quoted paths (git escapes
        /// names with special chars) are returned as-is including quotes, which
        /// keeps them OUT of the bookkeeping allowlist so they correctly block.
        /// </summary>
        internal static System.Collections.Generic.List<string> ParsePorcelainPaths(string porcelain)
        {
            var paths = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(porcelain)) return paths;

            foreach (var rawLine in porcelain.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Status is the first 2 chars + a space; path begins at index 3.
                if (rawLine.Length <= 3) continue;
                string path = rawLine.Substring(3).Trim();

                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    path = path.Substring(arrow + 4).Trim();
                }

                path = path.Replace('\\', '/');
                if (path.Length > 0) paths.Add(path);
            }
            return paths;
        }

        /// <summary>
        /// True when <paramref name="repoRelativePath"/> (forward-slash) is an
        /// MT-managed bookkeeping file safe to auto-commit before a merge.
        /// </summary>
        internal static bool IsBookkeepingPath(string repoRelativePath)
        {
            if (string.IsNullOrEmpty(repoRelativePath)) return false;
            foreach (var allowed in BookkeepingPaths)
            {
                if (string.Equals(repoRelativePath, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Best-effort resolution of the repository's default branch, used to guard
        /// against merging a task branch into a non-trunk branch the main checkout
        /// happens to be parked on (task 90c2acc6, Suspect B). Returns <c>null</c>
        /// when the default cannot be determined UNAMBIGUOUSLY — callers treat null
        /// as "skip the assertion" so a legitimate merge is never blocked by a guess.
        /// </summary>
        private static async Task<string> DetectDefaultBranchAsync(string repoRoot)
        {
            // (2) Remote's published default (origin/HEAD -> origin/<branch>).
            var remote = await GitExec.RunAsync(repoRoot, "symbolic-ref", "--short", "refs/remotes/origin/HEAD").ConfigureAwait(false);
            if (remote.ExitCode == 0)
            {
                string r = remote.Stdout.Trim();
                const string prefix = "origin/";
                if (r.StartsWith(prefix, StringComparison.Ordinal)) r = r.Substring(prefix.Length);
                if (!string.IsNullOrEmpty(r)) return r;
            }

            // (3) The SOLE non-task local branch, if exactly one exists AND it bears a
            // conventional trunk name. Generalizing past hard-coded main/master to
            // custom trunk names was pipeline run 2 (Codex adversary HIGH: 'develop'
            // repos were over-blocked). But accepting an ARBITRARY lone branch was
            // itself unsafe — pipeline run 3 (Codex adversary HIGH): a lone
            // 'feature/parked' would be promoted to trunk and the task branch merged
            // into it, reopening the silent wrong-branch class. So we only trust the
            // lone branch when its name is a recognized default-branch convention.
            // Anything else (a lone 'feature/*', 'release', 'stable', ...) gives no
            // trustworthy signal: return null and let the caller fail closed. A repo
            // with MORE than one non-task branch is likewise ambiguous → null. Repos
            // with a genuinely unconventional trunk name must set the project's
            // git_default_branch (the authoritative source). Task branches
            // (task/<id>[--<slug>]) are excluded — they're never trunk.
            var branches = await GitExec.RunAsync(repoRoot, "for-each-ref", "--format=%(refname:short)", "refs/heads/").ConfigureAwait(false);
            if (branches.ExitCode == 0)
            {
                string sole = null;
                int count = 0;
                foreach (var raw in branches.Stdout.Split('\n'))
                {
                    string b = raw.Trim();
                    if (b.Length == 0 || b.StartsWith("task/", StringComparison.Ordinal)) continue;
                    count++;
                    sole = b;
                    if (count > 1) break;
                }
                if (count == 1 && IsConventionalTrunkName(sole)) return sole;
            }

            return null;
        }

        /// <summary>
        /// Recognized default-branch naming conventions. A lone local branch is only
        /// trusted as the trunk when it matches one of these — an arbitrary branch
        /// name carries no signal that it is actually the repository default (task
        /// 90c2acc6, pipeline run 3). Repos whose trunk is named otherwise must set
        /// the project's git_default_branch.
        /// </summary>
        private static readonly string[] ConventionalTrunkNames = { "main", "master", "develop", "trunk" };

        private static bool IsConventionalTrunkName(string branch)
        {
            if (string.IsNullOrWhiteSpace(branch)) return false;
            foreach (var name in ConventionalTrunkNames)
            {
                if (string.Equals(branch, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

    }

    /// <summary>
    /// Outcome of a Phase 3 auto-merge attempt. <see cref="Success"/> is true
    /// when either the merge committed cleanly OR the operation was skipped
    /// for a benign reason (e.g. no commits to merge, branch already gone).
    /// Failure populates <see cref="Stderr"/> and (for merge conflicts)
    /// <see cref="HadConflicts"/>.
    ///
    /// <para><see cref="Success"/> alone does NOT mean a merge happened — a benign
    /// skip is also a success. Callers that need to know whether the task branch
    /// actually landed in trunk (the activity feed, the janitor's recovery
    /// accounting) MUST check <see cref="Merged"/>, not <see cref="Success"/>.
    /// Conflating the two (task 90c2acc6, Suspect A) let a no-op be reported as
    /// "merged into trunk", masking the very failure this class describes.</para>
    /// </summary>
    public class MergeResult
    {
        public bool Success { get; set; }

        /// <summary>
        /// True ONLY when a real <c>git merge</c> (fast-forward or merge commit)
        /// landed the task branch in trunk. False for every benign skip
        /// (no commits to merge, branch already deleted, no worktree record) and
        /// for every failure. Distinct from <see cref="Success"/>, which is also
        /// true for benign skips.
        /// </summary>
        public bool Merged { get; set; }

        /// <summary>Trunk branch the merge targeted, or null on early-exit failure.</summary>
        public string MergedInto { get; set; }

        /// <summary>True when the merge was aborted due to conflict.</summary>
        public bool HadConflicts { get; set; }

        /// <summary>
        /// True when the merge subprocess was KILLED by the GitExec timeout rather
        /// than completing. The merge outcome is UNKNOWN — this is emphatically NOT
        /// a conflict (<see cref="HadConflicts"/> stays false), because conflating a
        /// timeout with a definitive conflict could drive destructive
        /// conflict-resolution on a merge that would otherwise have succeeded.
        /// Callers treat this as retry-later.
        /// </summary>
        public bool TimedOut { get; set; }

        /// <summary>
        /// True when a timed-out merge could NOT be cleaned up (the follow-up
        /// <c>merge --abort</c> itself failed/timed out), so the main checkout may
        /// be left HALF-MERGED. This is the loud, distinct hazard behind the
        /// mutating-timeout case: callers MUST surface it visibly (activity feed /
        /// Owner-visible signal) and must NOT proceed silently — a half-merged
        /// checkout nobody knows about is the real danger.
        /// </summary>
        public bool IndeterminateState { get; set; }

        public string Stderr { get; set; }

        /// <summary>
        /// Set when no merge happened for a benign reason. Lifecycle hook
        /// treats Success=true as the user-visible state regardless of
        /// SkippedReason being set.
        /// </summary>
        public string SkippedReason { get; set; }
    }

    /// <summary>
    /// Outcome of Phase 2.5 helper-branch integration (per-agent isolation,
    /// task bab81a92). <see cref="Success"/> is true when every helper branch was
    /// integrated into the canonical branch (or there were none / nothing to merge).
    /// On conflict, <see cref="Success"/> is false, <see cref="HadConflicts"/> is
    /// true, and <see cref="ConflictBranches"/> names the offending branch — the
    /// caller halts teardown and preserves all worktrees/branches.
    /// </summary>
    public class HelperIntegrationResult
    {
        public bool Success { get; set; }

        /// <summary>True when a helper merge conflicted (and was aborted).</summary>
        public bool HadConflicts { get; set; }

        /// <summary>True when a helper merge was KILLED by the GitExec timeout
        /// (outcome unknown — NOT a conflict). Retry-later.</summary>
        public bool TimedOut { get; set; }

        /// <summary>True when a timed-out helper merge could not be aborted, so the
        /// canonical worktree may be left half-merged — surface loudly, don't proceed.</summary>
        public bool IndeterminateState { get; set; }

        /// <summary>
        /// Helper branches integrated into the canonical branch (including those
        /// with no commits ahead). These are the branches to delete after prune-all.
        /// </summary>
        public System.Collections.Generic.List<string> IntegratedBranches { get; set; }
            = new System.Collections.Generic.List<string>();

        /// <summary>Helper branches whose merge conflicted and was aborted.</summary>
        public System.Collections.Generic.List<string> ConflictBranches { get; set; }
            = new System.Collections.Generic.List<string>();

        public string Stderr { get; set; }

        /// <summary>Set when integration was a no-op for a benign reason.</summary>
        public string SkippedReason { get; set; }
    }
}
