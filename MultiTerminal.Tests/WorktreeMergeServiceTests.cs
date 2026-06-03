using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression coverage for <see cref="WorktreeMergeService.MergeForTaskAsync"/>
    /// — the Phase-3 auto-merge that lands a completed task's <c>task/{id}</c>
    /// branch into the main checkout's trunk (task 90c2acc6: "auto-merge silently
    /// fails to land branch in master").
    ///
    /// <para>Each test stands up a throwaway git repo on disk plus an isolated
    /// SQLite DB (via the MULTITERMINAL_TEST_DB override) holding the canonical
    /// worktree row the service looks up. The merge runs real <c>git</c>
    /// subprocesses against that repo, so these are integration tests, not mocks.</para>
    ///
    /// <para>These are CHARACTERIZATION tests: they assert the service's behavior
    /// AS IT IS TODAY so the failing path is pinned before the fix. Comments tag
    /// each scenario with the suspect (A / B) it documents; the assertions that
    /// encode buggy behavior are flipped to the correct expectation in the fix
    /// items (2 / 3).</para>
    /// </summary>
    public sealed class WorktreeMergeServiceTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _repoRoot;
        private readonly TaskDatabase _db;

        // 8-char id so ShortId(taskId) == taskId and the canonical branch is a
        // stable, predictable "task/abcd1234".
        private const string TaskId = "abcd1234";
        private const string CanonicalBranch = "task/abcd1234";

        public WorktreeMergeServiceTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"mt_merge_test_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _db = new TaskDatabase();

            _repoRoot = Path.Combine(Path.GetTempPath(), $"mt_merge_repo_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_repoRoot);
            InitRepo(_repoRoot);
        }

        public void Dispose()
        {
            _db?.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            TryDelete(_testDbPath);
            TryDeleteDir(_repoRoot);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Happy path: the task branch is one commit ahead of master, the main
        /// checkout sits on master. The commit must land in master and the branch
        /// must be deleted, with Success and no SkippedReason. This is the core
        /// "does the branch reach master?" assertion.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task BranchAheadOfMaster_LandsCommitInMaster()
        {
            string sha = CommitOnTaskBranchAheadOfMaster("feature work on task branch");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot);

            Assert.True(result.Success, $"merge should succeed; stderr: {result.Stderr}");
            Assert.True(result.Merged, "a real merge must be reported as Merged");
            Assert.Null(result.SkippedReason);
            Assert.Equal("master", result.MergedInto);
            Assert.True(IsAncestor(sha, "master"), "task commit must be reachable from master after merge");
            Assert.False(BranchExists(CanonicalBranch), "task branch should be deleted after a clean merge");
        }

        /// <summary>
        /// Suspect A (FIXED, item 2) — a benign skip must be distinguishable from a
        /// real merge. When the task branch has no commits beyond trunk, the service
        /// deletes the branch and returns Success=true — but Merged MUST be false so
        /// callers (janitor Pass-2, activity feed) don't report "merged into trunk"
        /// for a no-op.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task BranchWithNoCommitsAhead_SkipsAndReportsNotMerged()
        {
            // Create task branch at master's tip — no commits ahead.
            RunGit(_repoRoot, "branch", CanonicalBranch);
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot);

            Assert.True(result.Success);
            Assert.False(result.Merged, "no merge happened — Merged must be false even though Success is true");
            Assert.NotNull(result.SkippedReason);
            Assert.False(BranchExists(CanonicalBranch), "empty branch is deleted");
        }

        /// <summary>
        /// Suspect B (FIXED, item 3) — trunk must be the default branch, not just
        /// "whatever HEAD points to". With the main checkout parked on a non-default
        /// branch, the merge must REFUSE (Success=false, Merged=false) and preserve
        /// the task branch rather than silently landing it in the wrong branch.
        /// Default branch is auto-detected here (only 'master' exists locally).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task MainCheckoutOnNonDefaultBranch_RefusesAndPreservesBranch()
        {
            string sha = CommitOnTaskBranchAheadOfMaster("work that should reach master");
            // Park the main checkout on a non-trunk branch.
            RunGit(_repoRoot, "checkout", "-b", "feature/parked");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot);

            Assert.False(result.Success, "merge into a non-default branch must be refused");
            Assert.False(result.Merged);
            Assert.False(IsAncestor(sha, "feature/parked"), "must NOT merge into the parked branch");
            Assert.False(IsAncestor(sha, "master"), "branch is preserved for manual resolution, not yet in master");
            Assert.True(BranchExists(CanonicalBranch), "task branch preserved on refusal");
            Assert.Contains("feature/parked", result.Stderr);
        }

        /// <summary>
        /// The explicit expectedTrunk override (the project's configured
        /// git_default_branch) is authoritative: even when the main checkout is on a
        /// branch that auto-detection would accept, a mismatch with the configured
        /// trunk refuses the merge.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task ExplicitExpectedTrunkMismatch_Refuses()
        {
            string sha = CommitOnTaskBranchAheadOfMaster("work");
            // Main checkout stays on master, but the project says trunk is "main".
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot, expectedTrunk: "main");

            Assert.False(result.Success);
            Assert.False(result.Merged);
            Assert.False(IsAncestor(sha, "master"), "refused — nothing landed");
            Assert.True(BranchExists(CanonicalBranch));
        }

        /// <summary>
        /// FAIL CLOSED (pipeline run 1, Codex security + adversary HIGH): when the
        /// default branch cannot be determined unambiguously — no configured trunk,
        /// no origin/HEAD, and BOTH main and master exist locally — the merge must
        /// REFUSE rather than fall back to merging into whatever HEAD is parked on.
        /// The old fail-open behavior reopened the silent wrong-branch bug.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task AmbiguousDefaultBranch_FailsClosed()
        {
            string sha = CommitOnTaskBranchAheadOfMaster("work");
            // Create a stale 'main' alongside 'master' so detection is ambiguous, then
            // park the main checkout on a feature branch. No origin/HEAD, no expectedTrunk.
            RunGit(_repoRoot, "branch", "main");
            RunGit(_repoRoot, "checkout", "-b", "feature/parked");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot);

            Assert.False(result.Success, "ambiguous trunk must fail closed, not merge blind");
            Assert.False(result.Merged);
            Assert.False(IsAncestor(sha, "feature/parked"), "must NOT merge into the parked branch");
            Assert.False(IsAncestor(sha, "master"));
            Assert.True(BranchExists(CanonicalBranch), "task branch preserved on refusal");
            Assert.Contains("default branch", result.Stderr);
        }

        /// <summary>
        /// Custom trunk name (pipeline run 2, Codex adversary HIGH): a legitimate
        /// single-trunk repo whose trunk is NOT main/master (e.g. 'develop'), with no
        /// origin/HEAD and no configured git_default_branch, must still auto-merge.
        /// Detection resolves the SOLE non-task local branch, so the fail-closed guard
        /// does not over-block these repos.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CustomTrunkSoleNonTaskBranch_Merges()
        {
            // Rename the only branch master -> develop, then put a commit ahead on the task branch.
            RunGit(_repoRoot, "branch", "-m", "master", "develop");
            RunGit(_repoRoot, "checkout", "-b", CanonicalBranch);
            File.WriteAllText(Path.Combine(_repoRoot, "work.txt"), "custom trunk work");
            RunGit(_repoRoot, "add", "work.txt");
            RunGit(_repoRoot, "commit", "-m", "custom trunk work");
            string sha = RunGit(_repoRoot, "rev-parse", "HEAD").Trim();
            RunGit(_repoRoot, "checkout", "develop");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot);

            Assert.True(result.Success, $"sole-non-task-branch trunk should merge; stderr: {result.Stderr}");
            Assert.True(result.Merged);
            Assert.Equal("develop", result.MergedInto);
            Assert.True(IsAncestor(sha, "develop"), "commit lands in the custom trunk");
            Assert.False(BranchExists(CanonicalBranch), "task branch deleted after clean merge");
        }

        /// <summary>
        /// FAIL CLOSED on a lone NON-conventional branch (pipeline run 3, Codex
        /// adversary HIGH): if the only non-task local branch is a feature branch
        /// (not a recognized trunk name) with no origin/HEAD and no configured
        /// git_default_branch, the lone branch must NOT be promoted to trunk — the
        /// merge refuses rather than merging into an arbitrary branch.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task SoleNonConventionalBranch_FailsClosed()
        {
            // The only non-task branch is a feature branch (not main/master/develop/trunk).
            RunGit(_repoRoot, "branch", "-m", "master", "feature/solo");
            RunGit(_repoRoot, "checkout", "-b", CanonicalBranch);
            File.WriteAllText(Path.Combine(_repoRoot, "work.txt"), "work");
            RunGit(_repoRoot, "add", "work.txt");
            RunGit(_repoRoot, "commit", "-m", "work");
            string sha = RunGit(_repoRoot, "rev-parse", "HEAD").Trim();
            RunGit(_repoRoot, "checkout", "feature/solo");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot);

            Assert.False(result.Success, "a lone non-conventional branch must not be assumed to be trunk");
            Assert.False(result.Merged);
            Assert.False(IsAncestor(sha, "feature/solo"), "must NOT merge into the lone feature branch");
            Assert.True(BranchExists(CanonicalBranch), "task branch preserved on refusal");
        }

        /// <summary>
        /// The MT bookkeeping carve-out: a dirty, tracked <c>.claude/project.json</c>
        /// in the main checkout is auto-committed as a chore commit so the merge
        /// precondition (clean trunk) is met, rather than blocking the merge.
        /// Confirms the carve-out still works (relevant: project.json is dirty in
        /// the real repo during the very task-done flow that triggers the merge).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DirtyBookkeepingFile_AutoCommittedThenMerges()
        {
            // Track .claude/project.json on master, then leave it dirty.
            string claudeDir = Path.Combine(_repoRoot, ".claude");
            Directory.CreateDirectory(claudeDir);
            string projectJson = Path.Combine(claudeDir, "project.json");
            File.WriteAllText(projectJson, "{\"v\":1}");
            RunGit(_repoRoot, "add", ".claude/project.json");
            RunGit(_repoRoot, "commit", "-m", "add project.json");

            string sha = CommitOnTaskBranchAheadOfMaster("real work");
            File.WriteAllText(projectJson, "{\"v\":2}"); // dirty, uncommitted bookkeeping churn
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db).MergeForTaskAsync(TaskId, _repoRoot);

            Assert.True(result.Success, $"merge should proceed past the bookkeeping carve-out; stderr: {result.Stderr}");
            Assert.True(result.Merged, "a real merge happened after the carve-out commit");
            Assert.True(IsAncestor(sha, "master"), "task commit lands in master");
            Assert.True(IsWorkingTreeClean(_repoRoot), "bookkeeping file should have been committed, not left dirty");
        }

        // ---- helpers ------------------------------------------------------

        private void SaveCanonicalRow() =>
            _db.SaveWorktreeRecord(
                TaskId,
                agentName: "Alice",
                worktreePath: Path.Combine(_repoRoot, ".claude", "worktrees", TaskId),
                branchName: CanonicalBranch,
                isCanonical: true);

        /// <summary>
        /// Branch off master, commit one file change on the task branch, return to
        /// master, and return the task commit's SHA.
        /// </summary>
        private string CommitOnTaskBranchAheadOfMaster(string message)
        {
            RunGit(_repoRoot, "checkout", "-b", CanonicalBranch);
            File.WriteAllText(Path.Combine(_repoRoot, "work.txt"), message);
            RunGit(_repoRoot, "add", "work.txt");
            RunGit(_repoRoot, "commit", "-m", message);
            string sha = RunGit(_repoRoot, "rev-parse", "HEAD").Trim();
            RunGit(_repoRoot, "checkout", "master");
            return sha;
        }

        private static void InitRepo(string dir)
        {
            RunGit(dir, "init", "-b", "master");
            RunGit(dir, "config", "user.email", "test@mt.local");
            RunGit(dir, "config", "user.name", "MT Test");
            RunGit(dir, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(dir, "README.md"), "seed");
            RunGit(dir, "add", "README.md");
            RunGit(dir, "commit", "-m", "initial commit");
        }

        private bool BranchExists(string branch) =>
            !string.IsNullOrWhiteSpace(RunGit(_repoRoot, "branch", "--list", branch).Trim());

        private bool IsAncestor(string sha, string branch) =>
            RunGitExit(_repoRoot, out _, "merge-base", "--is-ancestor", sha, branch) == 0;

        private static bool IsWorkingTreeClean(string dir) =>
            string.IsNullOrWhiteSpace(RunGit(dir, "status", "--porcelain").Trim());

        /// <summary>Runs git, asserting exit 0, returning stdout.</summary>
        private static string RunGit(string workingDir, params string[] args)
        {
            int exit = RunGitExit(workingDir, out string stdout, args);
            Assert.True(exit == 0, $"git {string.Join(' ', args)} failed (exit {exit}) in {workingDir}");
            return stdout;
        }

        private static int RunGitExit(string workingDir, out string stdout, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
