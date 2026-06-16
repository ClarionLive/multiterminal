using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression coverage for <see cref="WorktreeManager.CreateForTaskAsync(string, string, bool, string)"/>
    /// — specifically the repoRoot→main-checkout normalization that stops a worktree
    /// from being created NESTED inside another worktree (task 25020dfa).
    ///
    /// <para>Background: callers pass the registered <c>Project.Path</c> as
    /// <c>repoRoot</c>. The path template <c>{repoRoot}/.claude/worktrees/&lt;id&gt;</c>
    /// trusted that string to be the MAIN checkout. When it had drifted to (or was)
    /// a linked worktree, every new worktree nested one level deeper — observed live
    /// in ClarionAssistant as <c>.../worktrees/0a2ac0cb/.claude/worktrees/&lt;id&gt;</c>.
    /// The fix resolves the true main root via <c>git rev-parse --git-common-dir</c>
    /// before building any path.</para>
    ///
    /// <para>Like <see cref="WorktreeMergeServiceTests"/>, each test stands up a
    /// throwaway git repo on disk plus an isolated SQLite DB (via the
    /// <c>MULTITERMINAL_TEST_DB</c> override) and runs real <c>git</c> subprocesses —
    /// these are integration tests, not mocks.</para>
    /// </summary>
    public sealed class WorktreeManagerTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _repoRoot;
        private readonly TaskDatabase _db;

        // 8-char id so ShortId(taskId) == taskId and the canonical worktree dir +
        // branch are stable/predictable ("wtm12345" / "task/wtm12345").
        private const string TaskId = "wtm12345";

        public WorktreeManagerTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"mt_wtmgr_test_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _db = new TaskDatabase();

            _repoRoot = Path.Combine(Path.GetTempPath(), $"mt_wtmgr_repo_{Guid.NewGuid():N}");
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
        /// Baseline: when repoRoot IS the main checkout, the canonical worktree lands
        /// flat at <c>{mainRoot}/.claude/worktrees/&lt;id&gt;</c> — one worktrees
        /// segment, no nesting. Confirms the normalization is a no-op on the happy path.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task MainCheckoutRepoRoot_CreatesFlatWorktree()
        {
            var mgr = new WorktreeManager(_db);

            var wt = await mgr.CreateForTaskAsync(TaskId, "Alice", isAssignee: true, _repoRoot);

            string expected = Path.Combine(_repoRoot, ".claude", "worktrees", TaskId);
            Assert.Equal(Norm(expected), Norm(wt.WorktreePath));
            Assert.True(Directory.Exists(wt.WorktreePath), "worktree dir should exist on disk");
            Assert.Equal(1, WorktreesSegmentCount(wt.WorktreePath)); // not nested
        }

        /// <summary>
        /// THE BUG REPRO (task 25020dfa): when repoRoot is itself a LINKED worktree,
        /// the new worktree must still be created as a FLAT SIBLING under the main
        /// checkout — NOT nested inside the linked worktree. Post-fix the
        /// --git-common-dir normalization collapses repoRoot to the main root first.
        /// <para>Pre-fix failure mode depends on the linked worktree's branch: in THIS
        /// fixture it sits on a <c>task/</c> branch, so pre-fix
        /// <c>ResolveTrunkAsync(repoRoot=linkedWorktree)</c> would have THROWN (task-branch
        /// HEAD). The literal nesting (<c>{linkedWorktree}/.claude/worktrees/&lt;id&gt;</c>)
        /// would instead manifest if the linked worktree were parked on a non-task branch.
        /// Either way the post-fix flat-sibling outcome this test asserts is what matters.</para>
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task LinkedWorktreeRepoRoot_NormalizesToMainRoot_NotNested()
        {
            // Stand up a linked worktree of the main repo and hand ITS path in as repoRoot.
            string linkedWorktree = Path.Combine(_repoRoot, ".claude", "worktrees", "aaaa0001");
            RunGit(_repoRoot, "worktree", "add", "-b", "task/aaaa0001", linkedWorktree);

            var mgr = new WorktreeManager(_db);
            var wt = await mgr.CreateForTaskAsync(TaskId, "Alice", isAssignee: true, linkedWorktree);

            // Must be the flat sibling under the MAIN checkout, never under the linked one.
            string expectedFlat = Path.Combine(_repoRoot, ".claude", "worktrees", TaskId);
            Assert.Equal(Norm(expectedFlat), Norm(wt.WorktreePath));
            Assert.Equal(1, WorktreesSegmentCount(wt.WorktreePath)); // single worktrees segment = not nested
            Assert.False(
                Norm(wt.WorktreePath).StartsWith(Norm(linkedWorktree) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"new worktree must not be nested under the linked worktree (got: {wt.WorktreePath})");
            Assert.True(Directory.Exists(wt.WorktreePath));
        }

        /// <summary>
        /// Fail loud: a directory that exists but is NOT inside a git repo can't be
        /// resolved to a main checkout, so creation throws rather than silently
        /// building (and potentially nesting) a path.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task NonGitRepoRoot_Throws()
        {
            string nonGit = Path.Combine(Path.GetTempPath(), $"mt_wtmgr_nongit_{Guid.NewGuid():N}");
            Directory.CreateDirectory(nonGit);
            try
            {
                var mgr = new WorktreeManager(_db);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await mgr.CreateForTaskAsync(TaskId, "Alice", isAssignee: true, nonGit));
            }
            finally
            {
                TryDeleteDir(nonGit);
            }
        }

        /// <summary>
        /// The helper-worktree path (isAssignee=false) routes EnsureCanonicalBranchAsync,
        /// trunk resolution, and `git worktree add` through the normalized root too. With
        /// a linked worktree handed in as repoRoot, the helper worktree must still land as
        /// a FLAT SIBLING under the main checkout, not nested. Asserted structurally so the
        /// test doesn't depend on the exact helper dir-name slug.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task HelperWorktree_LinkedRepoRoot_NormalizesToMainRoot_NotNested()
        {
            string linkedWorktree = Path.Combine(_repoRoot, ".claude", "worktrees", "aaaa0001");
            RunGit(_repoRoot, "worktree", "add", "-b", "task/aaaa0001", linkedWorktree);

            var mgr = new WorktreeManager(_db);
            var wt = await mgr.CreateForTaskAsync(TaskId, "Bob", isAssignee: false, linkedWorktree);

            string worktreesParent = Path.Combine(_repoRoot, ".claude", "worktrees");
            Assert.StartsWith(
                Norm(worktreesParent) + Path.DirectorySeparatorChar, Norm(wt.WorktreePath), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, WorktreesSegmentCount(wt.WorktreePath)); // not nested
            Assert.False(
                Norm(wt.WorktreePath).StartsWith(Norm(linkedWorktree) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                $"helper worktree must not be nested under the linked worktree (got: {wt.WorktreePath})");
            Assert.True(Directory.Exists(wt.WorktreePath));
        }

        /// <summary>
        /// Ordering invariant: an existing ACTIVE worktree short-circuits and is returned
        /// BEFORE repoRoot normalization runs. Proven by calling a second time with a
        /// non-git repoRoot — if normalization ran it would throw; instead the existing
        /// record is returned unchanged. Guards the lines 118–135 ordering against future
        /// edits.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task ExistingActiveWorktree_ShortCircuitsBeforeNormalization()
        {
            var mgr = new WorktreeManager(_db);
            var first = await mgr.CreateForTaskAsync(TaskId, "Alice", isAssignee: true, _repoRoot);

            string nonGit = Path.Combine(Path.GetTempPath(), $"mt_wtmgr_nongit_idem_{Guid.NewGuid():N}");
            Directory.CreateDirectory(nonGit);
            try
            {
                // Same (taskId, agent) → looked up by the idempotent short-circuit, which
                // must return before ResolveMainCheckoutRootAsync would reject this non-git path.
                var second = await mgr.CreateForTaskAsync(TaskId, "Alice", isAssignee: true, nonGit);
                Assert.Equal(Norm(first.WorktreePath), Norm(second.WorktreePath));
                Assert.True(Directory.Exists(second.WorktreePath));
            }
            finally
            {
                TryDeleteDir(nonGit);
            }
        }

        // ---- helpers ------------------------------------------------------

        /// <summary>Canonicalize a path (resolves + normalizes separators) so
        /// comparisons survive git's forward-slash output vs. Path.Combine backslashes.</summary>
        private static string Norm(string path) => Path.GetFullPath(path);

        /// <summary>Count the number of <c>worktrees</c> path segments — a flat
        /// worktree has exactly one; a nested one has two or more.</summary>
        private static int WorktreesSegmentCount(string path) =>
            Norm(path)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Count(s => s.Equals("worktrees", StringComparison.OrdinalIgnoreCase));

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
