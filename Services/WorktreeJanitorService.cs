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
        public async Task<JanitorResult> SweepAsync(
            Func<string, string> getProjectPathForTask,
            JanitorActivityCallback recordActivity)
        {
            var result = new JanitorResult();

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

            // Summary entry — only if anything notable happened.
            if (recordActivity != null
                && (result.ReconciledMissing > 0 || result.PendingMerges.Count > 0 || result.Errors.Count > 0))
            {
                recordActivity(
                    "janitor_sweep",
                    $"Worktree janitor: reconciled {result.ReconciledMissing} missing, {result.PendingMerges.Count} pending merge, {result.Errors.Count} errors.",
                    null);
            }

            return result;
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

        public List<string> Errors { get; } = new List<string>();
    }
}
