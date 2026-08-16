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

        // ---- task b88e7017: which side of a trunk mismatch is wrong? -------

        /// <summary>
        /// THE CA DEBUGGER CASE. The configured default branch does not exist in the
        /// repo at all, so "check out '{wantTrunk}'" was never actionable advice.
        ///
        /// <para>MUST FAIL against the pre-b88e7017 code, which had no verdict at all
        /// and unconditionally told the operator to check out the missing branch.</para>
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task ConfiguredTrunkDoesNotExist_BlamesTheConfigNotTheCheckout()
        {
            CommitOnTaskBranchAheadOfMaster("work");
            SaveCanonicalRow();

            // Repo has only 'master'; the project is configured for a branch that
            // isn't here (CA Debugger had git_default_branch='master' with only 'main').
            var result = await new WorktreeMergeService(_db)
                .MergeForTaskAsync(TaskId, _repoRoot, expectedTrunk: "does-not-exist");

            Assert.False(result.Success, "a mismatch must still refuse — 90c2acc6's guard is not weakened");
            Assert.False(result.Merged);
            Assert.Equal(TrunkMismatchKind.ConfiguredTrunkMissing, result.TrunkMismatch);
            Assert.True(BranchExists(CanonicalBranch), "task branch preserved on refusal");
            Assert.Contains("git_default_branch", result.Stderr);
            Assert.DoesNotContain("check out 'does-not-exist'", result.Stderr);
        }

        /// <summary>
        /// THE MULTITERMINAL CASE. The configured trunk exists but is strictly behind
        /// the checkout — 'master' left 320 commits back when the repo moved to 'main'.
        /// Merging into it would strand the work off the line of development, which is
        /// exactly what the old remedy text instructed.
        ///
        /// <para>MUST FAIL against the pre-b88e7017 code.</para>
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task ConfiguredTrunkContradictedByOriginHead_BlamesTheConfigNotTheCheckout()
        {
            // 'stale' exists and is left behind; origin/HEAD publishes 'master' as the
            // real default — the shape MultiTerminal was in after master -> main.
            RunGit(_repoRoot, "branch", "stale");
            File.WriteAllText(Path.Combine(_repoRoot, "moved-on.txt"), "trunk advanced past 'stale'");
            RunGit(_repoRoot, "add", "moved-on.txt");
            RunGit(_repoRoot, "commit", "-m", "advance master beyond stale");
            PublishOriginHead("master");

            CommitOnTaskBranchAheadOfMaster("work");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db)
                .MergeForTaskAsync(TaskId, _repoRoot, expectedTrunk: "stale");

            Assert.False(result.Success, "a mismatch must still refuse — 90c2acc6's guard is not weakened");
            Assert.Equal(TrunkMismatchKind.ConfiguredTrunkStale, result.TrunkMismatch);
            Assert.True(BranchExists(CanonicalBranch), "task branch preserved on refusal");
            Assert.Contains("git_default_branch", result.Stderr);
            Assert.DoesNotContain("check out 'stale'", result.Stderr);
        }

        /// <summary>
        /// THE NEGATIVE CASE — the one proving b88e7017 did not weaken 90c2acc6.
        /// The configured trunk is live and NOT behind the checkout, so the checkout
        /// genuinely has wandered. Verdict and message must be the original ones.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task CheckoutGenuinelyOnWrongBranch_StillBlamesTheCheckout()
        {
            string sha = CommitOnTaskBranchAheadOfMaster("work that belongs in master");
            // Park the checkout on a branch that is NOT behind master (it is master's
            // tip plus its own commit), so the staleness probes cannot fire.
            RunGit(_repoRoot, "checkout", "-b", "feature/parked");
            File.WriteAllText(Path.Combine(_repoRoot, "parked.txt"), "unrelated work");
            RunGit(_repoRoot, "add", "parked.txt");
            RunGit(_repoRoot, "commit", "-m", "commit on the parked branch");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db)
                .MergeForTaskAsync(TaskId, _repoRoot, expectedTrunk: "master");

            Assert.False(result.Success);
            Assert.Equal(TrunkMismatchKind.CheckoutOnWrongBranch, result.TrunkMismatch);
            Assert.Contains("check out 'master'", result.Stderr);
            Assert.False(IsAncestor(sha, "feature/parked"), "must not land in the parked branch");
            Assert.True(BranchExists(CanonicalBranch), "task branch preserved on refusal");
        }

        /// <summary>
        /// ITEM ② AS AN ENFORCED INVARIANT, not a convention: a remedy may never tell
        /// the operator to move to a branch BEHIND the current checkout. That single
        /// property is what made the shipped advice destructive rather than merely
        /// unhelpful. Asserted across every verdict, so rewording a message later
        /// cannot silently reintroduce it.
        /// </summary>
        [Fact]
        public void ConfigProblemsNeverSendTheOperatorBranchHopping()
        {
            foreach (TrunkMismatchKind kind in Enum.GetValues(typeof(TrunkMismatchKind)))
            {
                string target = WorktreeMergeService.RemedyTargetBranch(kind, "stale");
                string message = WorktreeMergeService.BuildTrunkMismatchMessage(
                    kind, trunk: "main", wantTrunk: "stale", branchName: "task/abcd1234");

                bool isConfigVerdict = kind == TrunkMismatchKind.ConfiguredTrunkMissing
                                    || kind == TrunkMismatchKind.ConfiguredTrunkStale;

                if (isConfigVerdict)
                {
                    Assert.True(target == null,
                        $"verdict {kind} names a branch to check out ('{target}'), but this verdict means the "
                        + "CONFIG is wrong — sending the operator to that branch is the shipped defect this ticket fixes");
                }
                else
                {
                    // Everything else routes to the default arm, which DOES tell the
                    // operator to move — so it must name a real branch. None is included
                    // deliberately: it reaches that arm too, and returning null for it
                    // produced the literal "check out ''".
                    Assert.Equal("stale", target);
                }

                // The half that was missing in Run 1 (code-reviewer MAJOR): assert the
                // SHIPPED STRING agrees with the helper. Without this the loop compared
                // a ternary against itself while BuildTrunkMismatchMessage independently
                // re-derived "check out '{wantTrunk}'" — a tautology dressed as a gate.
                if (target == null)
                {
                    Assert.False(message.Contains("check out '", StringComparison.Ordinal),
                        $"verdict {kind} names no remedy branch, but its message still tells the operator to "
                        + $"check one out: {message}");
                }
                else
                {
                    Assert.Contains($"check out '{target}'", message, StringComparison.Ordinal);
                }
            }
        }

        /// <summary>
        /// The end-to-end form of the same property, and the one that actually pins the
        /// shipped harm: when origin/HEAD publishes a default, a refusal must never
        /// instruct the operator to check out some OTHER branch and merge there. Real
        /// text shipped was "check out 'master' … and re-mark the task done" while
        /// origin/HEAD said 'main' and 'master' was 320 commits stale.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task RefusalNeverTellsYouToCheckOutABranchOriginHeadContradicts()
        {
            RunGit(_repoRoot, "branch", "stale");
            PublishOriginHead("master");
            CommitOnTaskBranchAheadOfMaster("work");
            SaveCanonicalRow();

            var result = await new WorktreeMergeService(_db)
                .MergeForTaskAsync(TaskId, _repoRoot, expectedTrunk: "stale");

            Assert.False(result.Success);
            Assert.DoesNotContain("check out 'stale'", result.Stderr);
            Assert.Contains("origin/HEAD", result.Stderr);
        }

        /// <summary>
        /// PIPELINE RUN 1, DEBUGGER HIGH. An INCONCLUSIVE probe must never be read as
        /// "the configured trunk does not exist".
        ///
        /// <para>GitExec collapses a timeout AND a process-start failure onto
        /// ExitCode -1, while a genuinely absent ref is exit 1 — and GitExec's own docs
        /// require callers to treat TimedOut as retry-later, "NOT as evidence that a
        /// worktree/branch is gone". The first cut tested `ExitCode != 0`, so a wedged
        /// or missing git produced a confident ConfiguredTrunkMissing whose remedy tells
        /// the operator to set git_default_branch to whatever branch the checkout is
        /// parked on. When the checkout is the thing that is wrong — the case 90c2acc6
        /// exists to catch — that promotes a feature branch to project trunk.</para>
        ///
        /// <para>Driven through a NON-REPOSITORY directory, where git exits 128 rather
        /// than 1. Same guard branch as a timeout, but deterministic and fast — a real
        /// wedged-git fixture cannot be made reliable in a unit test.</para>
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task InconclusiveProbe_IsNotReadAsMissingTrunk()
        {
            string notARepo = Path.Combine(Path.GetTempPath(), $"mt_not_a_repo_{Guid.NewGuid():N}");
            Directory.CreateDirectory(notARepo);
            try
            {
                var kind = await WorktreeMergeService.ClassifyTrunkMismatchAsync(
                    notARepo, trunk: "feature/parked", wantTrunk: "master");

                Assert.Equal(TrunkMismatchKind.CheckoutOnWrongBranch, kind);
                Assert.NotEqual(TrunkMismatchKind.ConfiguredTrunkMissing, kind);
            }
            finally
            {
                TryDeleteDir(notARepo);
            }
        }

        /// <summary>
        /// Pins the exit-code premise the guard rests on, so a future git version that
        /// changed it would fail HERE rather than silently reopening the HIGH above:
        /// a missing ref in a real repo is exit 1, and a non-repository is not.
        /// </summary>
        [Fact]
        public void MissingRefAndBrokenRepo_HaveDifferentExitCodes()
        {
            int missingRef = RunGitExit(_repoRoot, out _, "rev-parse", "--verify", "--quiet", "refs/heads/nope");
            Assert.Equal(1, missingRef);

            string notARepo = Path.Combine(Path.GetTempPath(), $"mt_not_a_repo_{Guid.NewGuid():N}");
            Directory.CreateDirectory(notARepo);
            try
            {
                int brokenRepo = RunGitExit(notARepo, out _, "rev-parse", "--verify", "--quiet", "refs/heads/nope");
                Assert.True(brokenRepo > 1,
                    $"expected a non-repository to exit >1 (got {brokenRepo}); the classifier distinguishes "
                    + "'absent ref' (exit 1) from 'could not look' on exactly this boundary");
            }
            finally
            {
                TryDeleteDir(notARepo);
            }
        }

        /// <summary>
        /// Point origin/HEAD at a local branch without needing a real remote: mirror the
        /// branch into refs/remotes/origin/ and set the symbolic ref.
        /// </summary>
        private void PublishOriginHead(string branch)
        {
            string sha = RunGit(_repoRoot, "rev-parse", branch).Trim();
            RunGit(_repoRoot, "update-ref", $"refs/remotes/origin/{branch}", sha);
            RunGit(_repoRoot, "symbolic-ref", "refs/remotes/origin/HEAD", $"refs/remotes/origin/{branch}");
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
