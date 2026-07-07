using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Phase 2 of per-task worktree isolation (task 1211ba68): auto-commits
    /// any changes in a task's worktree before the lifecycle prune step runs.
    /// Closes the gap Phase 1 left wide open — without this, prune refuses
    /// on dirty worktrees and tasks with code changes get stuck.
    ///
    /// <para>Stateless service. Wraps <c>git -C {worktree} status / add / commit</c>
    /// via <see cref="Process"/> invocation. NEVER passes <c>--no-verify</c>
    /// (per CLAUDE.md and Git Safety Protocol — pre-commit hooks must run).
    /// Returns a structured <see cref="AutoCommitResult"/> instead of throwing
    /// on git failure, so the lifecycle hook in
    /// <c>MessageBroker.UpdateTaskStatus</c> can branch on the outcome.</para>
    /// </summary>
    public class WorktreeAutoCommitService
    {
        private readonly TaskDatabase _db;

        public WorktreeAutoCommitService(TaskDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Commits any changes in the task's worktree. Returns success when
        /// either: (a) a commit was created, or (b) the worktree was already
        /// clean (nothing to commit). Returns failure on any git error.
        ///
        /// <para>Decisions ratified during planning:
        /// (1) zero changes → SkippedReason='no changes', Success=true so
        /// lifecycle proceeds to prune;
        /// (2) existing manual commits + zero new uncommitted → same as (1);
        /// (3) <c>git add -A</c> (catches deletions);
        /// (4) single squash commit (no per-checklist-item history);
        /// (5) pre-commit hooks honored — failure surfaces stderr.</para>
        /// </summary>
        public Task<AutoCommitResult> CommitForTaskAsync(
            string taskId,
            string repoRoot,
            string taskTitle,
            string implementationSummary,
            string agentName)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            // Canonical (assignee) worktree — the representative record.
            var record = _db.GetWorktreeForTask(taskId);
            return CommitRecordAsync(taskId, record, repoRoot, taskTitle, implementationSummary, agentName);
        }

        /// <summary>
        /// Agent-aware counterpart of <see cref="CommitForTaskAsync"/>: commits a
        /// specific agent's worktree on the task (per-agent isolation, task bab81a92).
        /// Used by the task-done teardown to commit every helper worktree on its own
        /// branch before integration.
        /// </summary>
        public Task<AutoCommitResult> CommitForAgentAsync(
            string taskId,
            string agentName,
            string repoRoot,
            string taskTitle,
            string implementationSummary)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            var record = _db.GetWorktreeForTask(taskId, agentName);
            return CommitRecordAsync(taskId, record, repoRoot, taskTitle, implementationSummary, agentName);
        }

        private async Task<AutoCommitResult> CommitRecordAsync(
            string taskId,
            MCPServer.Models.TaskWorktree record,
            string repoRoot,
            string taskTitle,
            string implementationSummary,
            string agentName)
        {
            if (record == null || !string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return new AutoCommitResult
                {
                    Success = true,
                    SkippedReason = "no active worktree record"
                };
            }

            string worktreePath = record.WorktreePath;

            // CA3003: worktreePath comes from the task_worktrees table, written
            // only by WorktreeManager. Same trust model as WorktreeManager.
#pragma warning disable CA3003
            if (!Directory.Exists(worktreePath))
#pragma warning restore CA3003
            {
                return new AutoCommitResult
                {
                    Success = false,
                    Stderr = $"Worktree directory missing on disk: {worktreePath}"
                };
            }

            var statusResult = await GitExec.RunAsync(worktreePath, "status", "--porcelain").ConfigureAwait(false);
            if (statusResult.ExitCode != 0)
            {
                return new AutoCommitResult
                {
                    Success = false,
                    Stderr = $"git status failed: {statusResult.Stderr.Trim()}"
                };
            }

            var changedFiles = ParseChangedFiles(statusResult.Stdout);
            if (changedFiles.Count == 0)
            {
                return new AutoCommitResult
                {
                    Success = true,
                    SkippedReason = "no changes"
                };
            }

            var branchResult = await GitExec.RunAsync(worktreePath, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
            if (branchResult.ExitCode != 0)
            {
                return new AutoCommitResult
                {
                    Success = false,
                    Stderr = $"git rev-parse failed: {branchResult.Stderr.Trim()}"
                };
            }

            string currentBranch = branchResult.Stdout.Trim();
            if (!string.Equals(currentBranch, record.BranchName, StringComparison.Ordinal))
            {
                return new AutoCommitResult
                {
                    Success = false,
                    Stderr = $"Branch mismatch: worktree on '{currentBranch}', expected '{record.BranchName}'. Refusing to commit."
                };
            }

            var unlinkedFiles = ComputeUnlinkedFiles(taskId, repoRoot, changedFiles);

            // Stage the parsed file list explicitly rather than `git add -A`.
            // CLAUDE.md and the repo's safety hook forbid blanket -A/-. for
            // a good reason — it stages anything that drifted into the worktree
            // (rogue .env, credentials.json, large binaries, etc.). The
            // porcelain output already enumerated exactly what changed, so we
            // pass that list verbatim. The `--` separator prevents any path
            // starting with `-` from being interpreted as a flag.
            var addArgs = new List<string> { "add", "--" };
            addArgs.AddRange(changedFiles);
            var addResult = await GitExec.RunAsync(worktreePath, addArgs.ToArray()).ConfigureAwait(false);
            if (addResult.ExitCode != 0)
            {
                return new AutoCommitResult
                {
                    Success = false,
                    Stderr = $"git add failed: {addResult.Stderr.Trim()}"
                };
            }

            string message = BuildCommitMessage(taskId, taskTitle, implementationSummary, agentName);

            var commitResult = await GitExec.RunAsync(worktreePath, "commit", "-m", message).ConfigureAwait(false);
            if (commitResult.ExitCode != 0)
            {
                return new AutoCommitResult
                {
                    Success = false,
                    Stderr = $"git commit failed: {commitResult.Stderr.Trim()}; stdout: {commitResult.Stdout.Trim()}"
                };
            }

            var hashResult = await GitExec.RunAsync(worktreePath, "rev-parse", "HEAD").ConfigureAwait(false);
            string commitHash = hashResult.ExitCode == 0
                ? hashResult.Stdout.Trim()
                : null;

            return new AutoCommitResult
            {
                Success = true,
                CommitHash = commitHash,
                ChangedFiles = changedFiles,
                UnlinkedFiles = unlinkedFiles
            };
        }

        private List<string> ComputeUnlinkedFiles(string taskId, string repoRoot, List<string> changedFiles)
        {
            var linkedRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var link in _db.GetFileLinksForTask(taskId))
                {
                    if (string.IsNullOrEmpty(link.FilePath)) continue;
                    string rel;
                    try
                    {
                        rel = Path.GetRelativePath(repoRoot, link.FilePath).Replace('\\', '/');
                    }
                    catch
                    {
                        continue;
                    }
                    linkedRelative.Add(rel);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WorktreeAutoCommit] Failed to load task_file_links for {taskId}: {ex.Message}");
                return new List<string>();
            }

            return changedFiles.Where(f => !linkedRelative.Contains(f)).ToList();
        }

        private static string BuildCommitMessage(
            string taskId, string taskTitle, string implementationSummary, string agentName)
        {
            var sb = new StringBuilder();
            sb.AppendLine(taskTitle ?? "Task auto-commit");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(implementationSummary)
                ? "Auto-committed at task done."
                : implementationSummary.Trim());
            sb.AppendLine();
            sb.AppendLine($"Task: {taskId}");
            if (!string.IsNullOrEmpty(agentName))
            {
                sb.AppendLine();
                sb.Append($"Co-Authored-By: {agentName} <noreply@anthropic.com>");
            }
            return sb.ToString();
        }

        private static List<string> ParseChangedFiles(string porcelainOutput)
        {
            var files = new List<string>();
            if (string.IsNullOrWhiteSpace(porcelainOutput)) return files;

            using var reader = new StringReader(porcelainOutput);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length < 4) continue;
                // porcelain v1: "XY filename" where XY is 2 status chars + 1 space.
                string path = line.Substring(3).Trim();
                // Renamed entries are "XY old -> new" — keep the new path.
                int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    path = path.Substring(arrow + 4);
                }
                // Strip surrounding quotes git adds for paths with special chars.
                if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
                {
                    path = path.Substring(1, path.Length - 2);
                }
                if (!string.IsNullOrWhiteSpace(path)) files.Add(path);
            }
            return files;
        }

    }

    /// <summary>
    /// Outcome of a Phase 2 auto-commit attempt. <see cref="Success"/> is true
    /// when either a commit was created OR the worktree was already clean
    /// (in which case <see cref="SkippedReason"/> is set). Failure populates
    /// <see cref="Stderr"/>.
    /// </summary>
    public class AutoCommitResult
    {
        public bool Success { get; set; }

        /// <summary>SHA of the newly created commit, or null if no commit was made.</summary>
        public string CommitHash { get; set; }

        /// <summary>All files reported by <c>git status --porcelain</c> at commit time.</summary>
        public List<string> ChangedFiles { get; set; } = new List<string>();

        /// <summary>
        /// Subset of <see cref="ChangedFiles"/> that were NOT pre-linked to the
        /// task via <c>link_task_file</c>. Surfaced as an informational note in
        /// the activity feed. Phase 2.x may add a UI confirmation if this list
        /// is large.
        /// </summary>
        public List<string> UnlinkedFiles { get; set; } = new List<string>();

        /// <summary>Captured stderr (and tail of stdout) on failure. Null on success.</summary>
        public string Stderr { get; set; }

        /// <summary>
        /// Set when no commit was made for a benign reason (e.g. "no changes",
        /// "no active worktree record"). Lifecycle hook treats this as success.
        /// </summary>
        public string SkippedReason { get; set; }
    }
}
