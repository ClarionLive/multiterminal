using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Wraps <c>git worktree add</c> and <c>git worktree remove</c> via process
    /// invocation. Phase 1 of per-task worktree isolation (task e1a5c579) — gated
    /// by <c>MULTITERMINAL_WORKTREE_MODE</c> at the call site.
    ///
    /// <para>Stateless service; takes a <see cref="TaskDatabase"/> reference at
    /// construction. Each public method takes <c>repoRoot</c> per call so the
    /// same manager can serve multiple projects in one MT instance.</para>
    ///
    /// <para>Path conventions: a worktree for task <c>abcd1234</c> lives at
    /// <c>{repoRoot}/.claude/worktrees/abcd1234/</c> on branch <c>task/abcd1234</c>,
    /// forked from the current HEAD of <c>repoRoot</c> at create time. The
    /// <c>.claude/worktrees/</c> dir is a descendant of the repo root (gitignored)
    /// so that (a) every worktree is a descendant of the main checkout — important
    /// for Claude Code's permission scope, harness cwd pinning, AND the
    /// <c>EnterWorktree(path=...)</c> enter-existing form, which requires the target
    /// be a worktree under <c>.claude/worktrees/</c> for cwd-pinned-at-launch agents
    /// (task 0134ec2f) — and (b) all per-task scratch space is contained inside the
    /// project folder rather than floating in the parent dir.</para>
    /// </summary>
    public class WorktreeManager
    {
        private readonly TaskDatabase _db;

        /// <summary>
        /// Creates a new WorktreeManager backed by the supplied
        /// <see cref="TaskDatabase"/> for record persistence.
        /// </summary>
        public WorktreeManager(TaskDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Materializes a git worktree for the given task and persists a record.
        /// Idempotent: if an <c>active</c> record exists AND its worktree path is
        /// on disk, the existing record is returned unchanged.
        /// </summary>
        /// <param name="taskId">Kanban task id (any length; the first 8 chars
        /// drive the path + branch name).</param>
        /// <param name="repoRoot">Filesystem path to the main checkout of the
        /// repository. Must exist and be a valid git repo.</param>
        /// <exception cref="InvalidOperationException">Thrown when
        /// <c>git worktree add</c> exits non-zero. The exception message
        /// includes the captured stderr.</exception>
        public async Task<TaskWorktree> CreateForTaskAsync(string taskId, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            // CA3003: taskId is an app-generated GUID (created by MessageBroker.CreateTask)
            // and repoRoot is a Project.Path field stored at registration time. Both
            // values are application-managed; existing.WorktreePath is read from the
            // task_worktrees table, which is only ever written by this class. None of
            // the path operations below take untrusted user-supplied path text.
#pragma warning disable CA3003
            if (!Directory.Exists(repoRoot))
                throw new DirectoryNotFoundException($"Repo root does not exist: {repoRoot}");

            var existing = _db.GetWorktreeForTask(taskId);
            if (existing != null
                && string.Equals(existing.Status, "active", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(existing.WorktreePath))
            {
                return existing;
            }

            string taskIdShort = taskId.Length >= 8 ? taskId.Substring(0, 8) : taskId;
            string trimmed = repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Worktrees live under .claude/worktrees/ (was <repoRoot>/worktrees/).
            // Required by Claude Code's EnterWorktree(path=...) enter-existing form:
            // for cwd-pinned-at-launch agents (= MT terminals) the target MUST be a
            // worktree under .claude/worktrees/ of the same repo. Still a descendant
            // of the main checkout, so the permission-scope invariant is preserved.
            // (Task 0134ec2f.)
            string worktreesParent = Path.Combine(trimmed, ".claude", "worktrees");
            string worktreePath = Path.Combine(worktreesParent, taskIdShort);
            string branchName = $"task/{taskIdShort}";

            if (!Directory.Exists(worktreesParent))
            {
                Directory.CreateDirectory(worktreesParent);
            }
#pragma warning restore CA3003

            var (exitCode, stdout, stderr) = await RunGitAsync(
                repoRoot, "worktree", "add", worktreePath, "-b", branchName).ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git worktree add failed (exit {exitCode}). stderr: {stderr.Trim()}; stdout: {stdout.Trim()}");
            }

            // Phase 1 smoke-test finding: a fresh worktree contains only tracked
            // files, but some build-required artifacts in this repo are vendored
            // and gitignored (notably tools/rg.exe, excluded by the user's global
            // *.exe rule). Without them dotnet build fails on a post-build copy
            // step. Seed the known vendored directories from the main checkout.
            // Phase 2 should generalize this into a per-project post-create hook
            // (e.g. a script in .claude/ that runs after worktree add).
            SeedVendoredArtifacts(repoRoot, worktreePath);

            _db.SaveWorktreeRecord(taskId, worktreePath, branchName);
            return _db.GetWorktreeForTask(taskId);
        }

        /// <summary>
        /// Removes the worktree associated with the given task and marks the
        /// record as <c>pruned</c>. Phase 1 does NOT pass <c>--force</c>, so
        /// uncommitted changes in the worktree will block the operation —
        /// callers should commit or stash first (Phase 2 will auto-commit).
        /// Branch deletion is deferred to Phase 3 auto-merge.
        /// </summary>
        /// <returns><c>true</c> on successful removal; <c>false</c> when no
        /// active record exists for this task (already pruned or never
        /// created).</returns>
        /// <exception cref="InvalidOperationException">Thrown when
        /// <c>git worktree remove</c> exits non-zero (typically uncommitted
        /// changes). Message includes captured stderr.</exception>
        public async Task<bool> PruneForTaskAsync(string taskId, string repoRoot)
        {
            if (string.IsNullOrEmpty(taskId))
                throw new ArgumentException("taskId is required", nameof(taskId));
            if (string.IsNullOrEmpty(repoRoot))
                throw new ArgumentException("repoRoot is required", nameof(repoRoot));

            var record = _db.GetWorktreeForTask(taskId);
            if (record == null) return false;
            if (string.Equals(record.Status, "pruned", StringComparison.OrdinalIgnoreCase))
                return false;

            // If the worktree directory is already gone (manual removal, etc.),
            // still need to tell git to forget it — `git worktree prune` cleans
            // up dangling administrative records.
            // CA3003: record.WorktreePath comes from task_worktrees, written only
            // by this class; not user-supplied.
#pragma warning disable CA3003
            if (!Directory.Exists(record.WorktreePath))
#pragma warning restore CA3003
            {
                await RunGitAsync(repoRoot, "worktree", "prune").ConfigureAwait(false);
                _db.MarkWorktreePruned(taskId);
                return true;
            }

            var (exitCode, stdout, stderr) = await RunGitAsync(
                repoRoot, "worktree", "remove", record.WorktreePath).ConfigureAwait(false);

            if (exitCode != 0)
            {
                // Windows partial-prune fallback. When an agent's terminal has
                // its cwd inside the worktree, `git worktree remove` wipes
                // contents + unregisters from .git/worktrees/, but cannot
                // rmdir the directory because the OS holds an open handle
                // through the child process's cwd. Git then exits non-zero
                // even though the meaningful work is done — the only residue
                // is an empty directory shell. This is the *common* case
                // when worktree mode is on (any agent working in the spawned
                // shell is naturally cwd'd inside the worktree at task-done
                // time). Detect by re-querying `git worktree list`; if the
                // path is gone from git's view, treat as partial success.
#pragma warning disable CA3003
                if (await IsWorktreeUnregisteredAsync(repoRoot, record.WorktreePath).ConfigureAwait(false))
                {
                    try
                    {
                        if (Directory.Exists(record.WorktreePath))
                        {
                            Directory.Delete(record.WorktreePath, recursive: false);
                        }
                    }
                    catch (Exception rmdirEx) when (rmdirEx is IOException || rmdirEx is UnauthorizedAccessException)
                    {
                        Debug.WriteLine(
                            $"[WorktreeManager] Worktree '{record.WorktreePath}' unregistered from git but rmdir failed (likely held by child cwd): {rmdirEx.Message}");
                    }
#pragma warning restore CA3003

                    _db.MarkWorktreePruned(taskId);
                    return true;
                }

                throw new InvalidOperationException(
                    $"git worktree remove failed (exit {exitCode}). stderr: {stderr.Trim()}; stdout: {stdout.Trim()}");
            }

            _db.MarkWorktreePruned(taskId);
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="worktreePath"/> is no
        /// longer listed by <c>git worktree list --porcelain</c> against
        /// <paramref name="repoRoot"/>. Used as a partial-success signal in
        /// <see cref="PruneForTaskAsync"/> on Windows when git wiped the
        /// worktree but couldn't rmdir the empty shell. Returns <c>false</c>
        /// when the list query itself fails — caller must treat that as a
        /// real failure.
        /// </summary>
        private static async Task<bool> IsWorktreeUnregisteredAsync(string repoRoot, string worktreePath)
        {
            var (exitCode, stdout, _) = await RunGitAsync(
                repoRoot, "worktree", "list", "--porcelain").ConfigureAwait(false);
            if (exitCode != 0) return false;

            string canonicalTarget = NormalizePath(worktreePath);
            using var reader = new StringReader(stdout ?? string.Empty);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.StartsWith("worktree ", StringComparison.Ordinal)) continue;
                string p = line.Substring("worktree ".Length).Trim();
                if (string.Equals(NormalizePath(p), canonicalTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                return Path.GetFullPath(path)
                    .Replace('/', Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException || ex is NotSupportedException)
            {
                return path.Replace('/', Path.DirectorySeparatorChar)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Returns the absolute path to the active worktree for this task, or
        /// <c>null</c> if no active record exists. Read-only DB lookup;
        /// performs no filesystem check.
        /// </summary>
        public string GetWorktreePathForTask(string taskId)
        {
            if (string.IsNullOrEmpty(taskId)) return null;
            var record = _db.GetWorktreeForTask(taskId);
            if (record == null) return null;
            return string.Equals(record.Status, "active", StringComparison.OrdinalIgnoreCase)
                ? record.WorktreePath
                : null;
        }

        /// <summary>
        /// Recursively copy gitignored-but-build-required artifacts from the
        /// main checkout into a fresh worktree. Phase 1 hardcodes a small
        /// allowlist (just "tools/" today); Phase 2 should replace this with a
        /// project-defined hook so each repo can declare its own seed list.
        /// Failures are surfaced as exceptions — a build that would silently
        /// fail later is worse than a loud failure here.
        /// </summary>
        // CA3003: repoRoot is a project-registered Project.Path field and worktreePath
        // is computed from it + an app-generated taskId GUID prefix. The seed-dir
        // names are a hardcoded literal allowlist. Path.Combine outputs are not
        // user-supplied path text. Same trust model as CreateForTaskAsync above.
#pragma warning disable CA3003
        private static void SeedVendoredArtifacts(string repoRoot, string worktreePath)
        {
            string[] seedDirs = { "tools" };
            foreach (string dir in seedDirs)
            {
                string source = Path.Combine(repoRoot, dir);
                if (!Directory.Exists(source)) continue;
                string dest = Path.Combine(worktreePath, dir);
                CopyDirectoryRecursive(source, dest);
            }
        }

        private static void CopyDirectoryRecursive(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
            {
                string target = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
            }
            foreach (string sub in Directory.GetDirectories(source))
            {
                string subDest = Path.Combine(dest, Path.GetFileName(sub));
                CopyDirectoryRecursive(sub, subDest);
            }
        }
#pragma warning restore CA3003

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
}
