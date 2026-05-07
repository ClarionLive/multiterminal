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
        public async Task<MergeResult> MergeForTaskAsync(string taskId, string repoRoot)
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

            var trunkResult = await RunGitAsync(repoRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
            if (trunkResult.exitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"rev-parse trunk failed: {trunkResult.stderr.Trim()}"
                };
            }

            string trunk = trunkResult.stdout.Trim();
            if (string.Equals(trunk, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = "Main checkout has detached HEAD; cannot merge."
                };
            }

            if (string.Equals(trunk, record.BranchName, StringComparison.Ordinal))
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"Main checkout is on the task's own branch ({trunk}); refusing to merge into self."
                };
            }

            var branchListResult = await RunGitAsync(repoRoot, "branch", "--list", record.BranchName).ConfigureAwait(false);
            if (branchListResult.exitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"branch --list failed: {branchListResult.stderr.Trim()}"
                };
            }
            if (string.IsNullOrWhiteSpace(branchListResult.stdout))
            {
                return new MergeResult
                {
                    Success = true,
                    SkippedReason = $"task branch '{record.BranchName}' already deleted"
                };
            }

            var aheadResult = await RunGitAsync(
                repoRoot, "log", "--oneline", $"{trunk}..{record.BranchName}").ConfigureAwait(false);
            if (aheadResult.exitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"git log {trunk}..{record.BranchName} failed: {aheadResult.stderr.Trim()}"
                };
            }
            if (string.IsNullOrWhiteSpace(aheadResult.stdout))
            {
                // Task branch has no commits beyond trunk — safe to delete without merging.
                var deleteEmpty = await RunGitAsync(repoRoot, "branch", "-d", record.BranchName).ConfigureAwait(false);
                if (deleteEmpty.exitCode != 0)
                {
                    return new MergeResult
                    {
                        Success = false,
                        Stderr = $"branch -d (empty) failed: {deleteEmpty.stderr.Trim()}"
                    };
                }
                return new MergeResult
                {
                    Success = true,
                    MergedInto = trunk,
                    SkippedReason = $"no commits to merge; branch '{record.BranchName}' deleted"
                };
            }

            // Refuse to merge if the main checkout has TRACKED uncommitted changes —
            // git would refuse anyway, but our message is clearer. Use
            // --untracked-files=no because untracked files don't block merges
            // unless they'd be overwritten, and git's own check catches that case.
            var dirtyCheck = await RunGitAsync(repoRoot, "status", "--porcelain", "--untracked-files=no").ConfigureAwait(false);
            if (dirtyCheck.exitCode != 0)
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"main-checkout status check failed: {dirtyCheck.stderr.Trim()}"
                };
            }
            if (!string.IsNullOrWhiteSpace(dirtyCheck.stdout))
            {
                return new MergeResult
                {
                    Success = false,
                    Stderr = $"Main checkout has uncommitted changes — cannot merge {record.BranchName} into {trunk}. Please commit or stash before closing this task."
                };
            }

            var mergeRunResult = await RunGitAsync(
                repoRoot, "merge", "--no-edit", record.BranchName).ConfigureAwait(false);
            if (mergeRunResult.exitCode != 0)
            {
                // Conflict (or other merge error) — abort to keep main clean.
                await RunGitAsync(repoRoot, "merge", "--abort").ConfigureAwait(false);
                return new MergeResult
                {
                    Success = false,
                    HadConflicts = true,
                    MergedInto = trunk,
                    Stderr = $"git merge failed (conflict or other error): {mergeRunResult.stderr.Trim()}; stdout: {mergeRunResult.stdout.Trim()}"
                };
            }

            // Use lowercase -d (refuses if not merged). The merge just succeeded
            // so this should always work; if it doesn't, surface as partial
            // success — the merge is durable, only the branch cleanup failed.
            var deleteBranchResult = await RunGitAsync(repoRoot, "branch", "-d", record.BranchName).ConfigureAwait(false);
            if (deleteBranchResult.exitCode != 0)
            {
                return new MergeResult
                {
                    Success = true,
                    MergedInto = trunk,
                    Stderr = $"merged but branch -d failed (manual cleanup needed): {deleteBranchResult.stderr.Trim()}"
                };
            }

            return new MergeResult
            {
                Success = true,
                MergedInto = trunk
            };
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
    /// Outcome of a Phase 3 auto-merge attempt. <see cref="Success"/> is true
    /// when either the merge committed cleanly OR the operation was skipped
    /// for a benign reason (e.g. no commits to merge, branch already gone).
    /// Failure populates <see cref="Stderr"/> and (for merge conflicts)
    /// <see cref="HadConflicts"/>.
    /// </summary>
    public class MergeResult
    {
        public bool Success { get; set; }

        /// <summary>Trunk branch the merge targeted, or null on early-exit failure.</summary>
        public string MergedInto { get; set; }

        /// <summary>True when the merge was aborted due to conflict.</summary>
        public bool HadConflicts { get; set; }

        public string Stderr { get; set; }

        /// <summary>
        /// Set when no merge happened for a benign reason. Lifecycle hook
        /// treats Success=true as the user-visible state regardless of
        /// SkippedReason being set.
        /// </summary>
        public string SkippedReason { get; set; }
    }
}
