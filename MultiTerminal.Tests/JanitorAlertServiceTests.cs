using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 94356803 — janitor alert routing. Two halves:
    /// (1) JanitorAlertService routing rules (pure, delegate-injected): which
    ///     actions alert, who receives them (team lead vs default recipient),
    ///     degradation on resolver failure, and the severe-tier push.
    /// (2) WorktreeJanitorService.ScanPendingMergesAsync (real temp SQLite + real
    ///     git repo): the read-only pending-merge scan honours the /stranded-style
    ///     falsifiability contract — "found none" is never conflated with
    ///     "couldn't look".
    /// </summary>
    public sealed class JanitorAlertServiceTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _repoRoot;
        private readonly TaskDatabase _db;

        // 8-char id so the canonical branch is a stable "task/feed1234".
        private const string TaskId = "feed1234";
        private const string CanonicalBranch = "task/feed1234";

        public JanitorAlertServiceTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"mt_janalert_test_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _db = new TaskDatabase();

            _repoRoot = Path.Combine(Path.GetTempPath(), $"mt_janalert_repo_{Guid.NewGuid():N}");
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

        // ---- half 1: JanitorAlertService routing --------------------------

        private sealed class Capture
        {
            public List<(string Recipient, string TaskId, string TaskTitle, string Type, string Summary, string CreatedBy)> Inbox { get; } = new();

            public List<(string Title, string Body)> Pushes { get; } = new();
        }

        private static JanitorAlertService BuildService(
            Capture capture,
            Func<string, (string Title, string ProjectId)> resolveTask = null,
            Func<string, string> resolveTeamLead = null,
            string defaultRecipient = "Owner")
        {
            return new JanitorAlertService(
                resolveTask: resolveTask ?? (_ => (null, null)),
                resolveTeamLead: resolveTeamLead ?? (_ => null),
                defaultRecipient: () => defaultRecipient,
                sendInbox: (recipient, taskId, taskTitle, type, summary, createdBy) =>
                {
                    capture.Inbox.Add((recipient, taskId, taskTitle, type, summary, createdBy));
                    return true;
                },
                sendSeverePush: (title, body) => capture.Pushes.Add((title, body)));
        }

        /// <summary>
        /// Housekeeping actions (recovered merges, orphan removals, plain sweep
        /// summaries) must stay feed-only — no inbox, no push.
        /// </summary>
        [Theory]
        [InlineData("janitor_merge_recovered")]
        [InlineData("janitor_orphan_removed")]
        [InlineData("janitor_deferred_prune")]
        [InlineData("janitor_reconciled_missing")]
        [InlineData("janitor_sweep")]
        [InlineData(null)]
        public void NonActionableAction_DoesNotAlert(string action)
        {
            var capture = new Capture();
            var svc = BuildService(capture);

            bool sent = svc.TryAlert(action, "some content", "t1");

            Assert.False(sent);
            Assert.Empty(capture.Inbox);
            Assert.Empty(capture.Pushes);
            Assert.False(JanitorAlertService.IsActionable(action));
        }

        /// <summary>
        /// The core routing rule: a pending merge on a project WITH a team lead goes
        /// to that lead (this is exactly the Owner→Charlie relay the ticket automates),
        /// with the janitor's content (incl. the git failure reason) as the summary
        /// and the dedicated janitor_alert inbox type. Not severe — no push.
        /// </summary>
        [Fact]
        public void PendingMerge_RoutesToProjectTeamLead()
        {
            var capture = new Capture();
            var svc = BuildService(
                capture,
                resolveTask: id => id == "a47a6cac" ? ("CA editor surfaces", "projCA") : (null, null),
                resolveTeamLead: pid => pid == "projCA" ? "Charlie" : null);

            bool sent = svc.TryAlert(
                "janitor_pending_merge",
                "Branch task/a47a6cac still alive for done task a47a6cac — merge it manually. Reason: merge conflict",
                "a47a6cac");

            Assert.True(sent);
            var msg = Assert.Single(capture.Inbox);
            Assert.Equal("Charlie", msg.Recipient);
            Assert.Equal("a47a6cac", msg.TaskId);
            Assert.Equal("CA editor surfaces", msg.TaskTitle);
            Assert.Equal(JanitorAlertService.InboxType, msg.Type);
            Assert.Contains("merge conflict", msg.Summary);
            Assert.Equal(JanitorAlertService.SenderName, msg.CreatedBy);
            Assert.Empty(capture.Pushes);
        }

        /// <summary>
        /// No team lead assigned → the alert falls back to the default inbox
        /// recipient (PM/Owner) instead of being dropped.
        /// </summary>
        [Fact]
        public void ProjectWithoutTeamLead_FallsBackToDefaultRecipient()
        {
            var capture = new Capture();
            var svc = BuildService(
                capture,
                resolveTask: _ => ("Title", "projNoLead"),
                resolveTeamLead: _ => null);

            Assert.True(svc.TryAlert("janitor_pending_merge", "branch alive", "t1"));

            Assert.Equal("Owner", Assert.Single(capture.Inbox).Recipient);
        }

        /// <summary>
        /// Task-less findings (sweep_attention summary lines carry relatedId=null)
        /// still alert — routed to the default recipient.
        /// </summary>
        [Fact]
        public void TasklessSweepAttention_RoutesToDefaultRecipient()
        {
            var capture = new Capture();
            var svc = BuildService(capture);

            Assert.True(svc.TryAlert("janitor_sweep_attention", "Worktree janitor: 2 errors.", null));

            var msg = Assert.Single(capture.Inbox);
            Assert.Equal("Owner", msg.Recipient);
            Assert.Null(msg.TaskId);
        }

        /// <summary>
        /// Resolution failures degrade to the default recipient rather than dropping
        /// the alert — mis-routed beats silent (the ticket's whole point).
        /// </summary>
        [Fact]
        public void ResolverThrows_DegradesToDefaultRecipient()
        {
            var capture = new Capture();
            var svc = BuildService(
                capture,
                resolveTask: _ => throw new InvalidOperationException("db gone"),
                resolveTeamLead: _ => throw new InvalidOperationException("db gone"));

            Assert.True(svc.TryAlert("janitor_pending_merge", "branch alive", "t1"));

            Assert.Equal("Owner", Assert.Single(capture.Inbox).Recipient);
            Assert.Empty(capture.Pushes);
        }

        /// <summary>
        /// Severe tier: a HALF-MERGED checkout (janitor_merge_indeterminate) sends the
        /// inbox alert AND the phone push; every other actionable action must not push.
        /// </summary>
        [Fact]
        public void SevereIndeterminate_PushesInAdditionToInbox()
        {
            var capture = new Capture();
            var svc = BuildService(capture, resolveTask: _ => ("T", "p"), resolveTeamLead: _ => "Charlie");

            Assert.True(svc.TryAlert("janitor_merge_indeterminate", "HALF-MERGED checkout — MANUAL CLEANUP NEEDED", "t1"));
            Assert.True(svc.TryAlert("janitor_pending_merge", "branch alive", "t1"));
            Assert.True(svc.TryAlert("janitor_merge_timeout", "timed out; rolled back", "t1"));
            Assert.True(svc.TryAlert("janitor_sweep_attention", "errors", null));

            Assert.Equal(4, capture.Inbox.Count);
            var push = Assert.Single(capture.Pushes);
            Assert.Contains("HALF-MERGED", push.Body);
            Assert.True(JanitorAlertService.IsSevere("janitor_merge_indeterminate"));
            Assert.False(JanitorAlertService.IsSevere("janitor_pending_merge"));
        }

        /// <summary>Empty content or an unresolvable recipient is a no-op, not a throw.</summary>
        [Fact]
        public void EmptyContentOrRecipient_NoAlert()
        {
            var capture = new Capture();
            var svc = BuildService(capture);
            Assert.False(svc.TryAlert("janitor_pending_merge", "", "t1"));

            var noRecipient = BuildService(capture, defaultRecipient: null);
            Assert.False(noRecipient.TryAlert("janitor_pending_merge", "content", "t1"));

            Assert.Empty(capture.Inbox);
        }

        // ---- half 2: ScanPendingMergesAsync -------------------------------

        /// <summary>
        /// A done task whose branch still exists in git is reported as a pending
        /// merge, and a fully-checked scan is Complete (count authoritative).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DoneTaskWithLiveBranch_IsReportedComplete()
        {
            RunGit(_repoRoot, "branch", CanonicalBranch);
            SavePrunedDoneRow();

            var scan = await new WorktreeJanitorService(_db).ScanPendingMergesAsync(_ => _repoRoot);

            Assert.True(scan.Complete, "every record was checked — scan must be complete");
            var item = Assert.Single(scan.Items);
            Assert.Equal(TaskId, item.TaskId);
            Assert.Equal(CanonicalBranch, item.BranchName);
            Assert.Equal(_repoRoot, item.RepoRoot);
        }

        /// <summary>
        /// Branch already gone (merged/deleted) → nothing pending, scan complete.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task DoneTaskWithDeletedBranch_ReportsNothing()
        {
            SavePrunedDoneRow(); // branch never created

            var scan = await new WorktreeJanitorService(_db).ScanPendingMergesAsync(_ => _repoRoot);

            Assert.True(scan.Complete);
            Assert.Empty(scan.Items);
        }

        /// <summary>
        /// Falsifiability: a record whose project can't be resolved was NOT checked —
        /// the scan must degrade to partial (Complete=false) instead of reading as a
        /// trustworthy "no pending merges".
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task UnresolvableProject_DegradesToPartial()
        {
            RunGit(_repoRoot, "branch", CanonicalBranch);
            SavePrunedDoneRow();

            var scan = await new WorktreeJanitorService(_db).ScanPendingMergesAsync(_ => null);

            Assert.False(scan.Complete, "an unchecked record must make the scan partial");
            Assert.Equal(1, scan.SkippedRecords);
            Assert.Empty(scan.Items);

            // Attribution contract (pipeline Run-1 debugger MEDIUM): each skipped record
            // carries its task id so a per-project consumer can attribute partiality to
            // ITS project instead of inheriting every other project's failures.
            Assert.Equal(new[] { TaskId }, scan.SkippedTaskIds);
        }

        /// <summary>
        /// Batched path (task 1ce9ddaf): several records against the SAME repo are
        /// answered from one `git for-each-ref` listing — live and deleted branches
        /// are still told apart correctly and the scan stays Complete.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task MultipleRecordsSameRepo_BatchListingStaysCorrect()
        {
            RunGit(_repoRoot, "branch", "task/alive001");
            SavePrunedDoneRow(taskId: "alive001", branchName: "task/alive001");
            SavePrunedDoneRow(taskId: "gone0002", branchName: "task/gone0002"); // branch never created

            var scan = await new WorktreeJanitorService(_db).ScanPendingMergesAsync(_ => _repoRoot);

            Assert.True(scan.Complete);
            var item = Assert.Single(scan.Items);
            Assert.Equal("alive001", item.TaskId);
            Assert.Equal("task/alive001", item.BranchName);
        }

        /// <summary>
        /// Falsifiability under the batch shape (task 1ce9ddaf): when the repo's
        /// branch listing itself fails (path is not a git repo), EVERY record of
        /// that repo counts as skipped — the scan degrades to partial instead of
        /// reading as a trustworthy "no pending merges".
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task RepoListingFailure_SkipsAllOfThatReposRecords()
        {
            string notARepo = Path.Combine(Path.GetTempPath(), $"mt_janalert_norepo_{Guid.NewGuid():N}");
            Directory.CreateDirectory(notARepo);
            try
            {
                SavePrunedDoneRow(taskId: "aaaa1111", branchName: "task/aaaa1111");
                SavePrunedDoneRow(taskId: "bbbb2222", branchName: "task/bbbb2222");

                var scan = await new WorktreeJanitorService(_db).ScanPendingMergesAsync(_ => notARepo);

                Assert.False(scan.Complete, "an unlistable repo means its records were NOT checked");
                Assert.Equal(2, scan.SkippedRecords);
                Assert.Contains("aaaa1111", scan.SkippedTaskIds);
                Assert.Contains("bbbb2222", scan.SkippedTaskIds);
                Assert.Empty(scan.Items);
            }
            finally
            {
                TryDeleteDir(notARepo);
            }
        }

        /// <summary>
        /// A record whose branch lies OUTSIDE refs/heads/task/ isn't covered by the
        /// batch listing — the per-record fallback probe must still find it.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task NonTaskNamespaceBranch_FallbackProbeStillReports()
        {
            RunGit(_repoRoot, "branch", "hotfix/feed1234");
            SavePrunedDoneRow(branchName: "hotfix/feed1234");

            var scan = await new WorktreeJanitorService(_db).ScanPendingMergesAsync(_ => _repoRoot);

            Assert.True(scan.Complete);
            var item = Assert.Single(scan.Items);
            Assert.Equal("hotfix/feed1234", item.BranchName);
        }

        /// <summary>ParseBranchNames: LF and CRLF output, whitespace, and empty input.</summary>
        [Fact]
        public void ParseBranchNames_HandlesLineEndingsAndEmpty()
        {
            var set = WorktreeJanitorService.ParseBranchNames("task/aaaa1111\r\ntask/bbbb2222\ntask/cccc3333\r\n");
            Assert.Equal(3, set.Count);
            Assert.Contains("task/aaaa1111", set);
            Assert.Contains("task/bbbb2222", set);
            Assert.Contains("task/cccc3333", set);

            Assert.Empty(WorktreeJanitorService.ParseBranchNames(string.Empty));
            Assert.Empty(WorktreeJanitorService.ParseBranchNames(null));
            Assert.Empty(WorktreeJanitorService.ParseBranchNames("\r\n\n"));
        }

        /// <summary>
        /// Task 36b0b9d5 item ③ (1ce9ddaf Run-1 debugger LOW). The listing now formats
        /// with <c>%(refname)</c>, which is unambiguous, and strips the literal
        /// <c>refs/heads/</c> prefix here.
        ///
        /// WHY THE FORMAT CHANGED: <c>%(refname:short)</c> shortens
        /// <c>refs/heads/task/foo</c> to <c>task/foo</c> ONLY while that name is
        /// unambiguous. Let a tag <c>refs/tags/task/foo</c> exist and git disambiguates
        /// by emitting <c>heads/task/foo</c> instead — the set lookup then misses and a
        /// LIVE branch reads as GONE, silently dropping its pending-merge finding. The
        /// failure direction is the dangerous one: absence of evidence rendered as
        /// evidence of absence, which is the exact conflation this scan's
        /// skip-attribution contract exists to prevent.
        /// </summary>
        [Fact]
        public void ParseBranchNames_StripsRefsHeadsPrefix_SoTagAmbiguityCannotHideALiveBranch()
        {
            var set = WorktreeJanitorService.ParseBranchNames(
                "refs/heads/task/aaaa1111\nrefs/heads/task/bbbb2222\n");

            Assert.Equal(2, set.Count);
            Assert.Contains("task/aaaa1111", set);
            Assert.Contains("task/bbbb2222", set);

            // Only the leading refs/heads/ is stripped — a branch whose own name
            // embeds the string must survive intact.
            var nested = WorktreeJanitorService.ParseBranchNames("refs/heads/task/refs/heads/odd\n");
            Assert.Contains("task/refs/heads/odd", nested);
        }

        // ---- half 3: Pass 2 batching (task 36b0b9d5 item ④) ----------------

        /// <summary>
        /// THE REGRESSION THIS ITEM COULD HAVE INTRODUCED. Pass 2 merges and DELETES
        /// branches as it walks, so a branch list cached at the top of the pass goes
        /// stale behind the pass's own mutations. A task with per-agent worktree rows
        /// yields several records naming the SAME branch; per-record probing used to
        /// see the branch already gone on the second one and skip, but a cached set
        /// still says "exists" and would drive a SECOND merge attempt at an
        /// already-handled branch. The taskId|branchName dedup is what prevents it.
        ///
        /// Without the dedup this fails with two merge attempts / two pending merges.
        /// </summary>
        [Fact]
        public async Task Pass2_DuplicateRowsForOneBranch_AttemptTheMergeOnlyOnce()
        {
            RunGit(_repoRoot, "branch", CanonicalBranch);
            SavePrunedDoneRow();
            SaveExtraPrunedAgentRow("Bob");   // same task, same branch, second agent
            SaveExtraPrunedAgentRow("Carol");

            int mergeAttempts = 0;
            var janitor = new WorktreeJanitorService(_db);

            var result = await janitor.SweepAsync(
                getProjectPathForTask: _ => _repoRoot,
                recordActivity: (_, _, _) => true,
                tryMergeForTask: (_, _) =>
                {
                    mergeAttempts++;
                    return Task.FromResult(new MergeResult { Merged = false, Success = false });
                });

            Assert.Equal(1, mergeAttempts);
            Assert.Single(result.PendingMerges);
        }

        /// <summary>
        /// Batch correctness: several DISTINCT branches in one repo are each classified
        /// correctly off a single listing — a live one is flagged, a never-created one
        /// is not.
        /// </summary>
        [Fact]
        public async Task Pass2_BatchedListing_ClassifiesLiveAndGoneBranchesIndependently()
        {
            RunGit(_repoRoot, "branch", "task/alive001");
            SavePrunedDoneRow(taskId: "alive001", branchName: "task/alive001");
            SavePrunedDoneRow(taskId: "gone0002", branchName: "task/gone0002"); // never created

            var janitor = new WorktreeJanitorService(_db);
            var result = await janitor.SweepAsync(
                getProjectPathForTask: _ => _repoRoot,
                recordActivity: (_, _, _) => true,
                tryMergeForTask: (_, _) => Task.FromResult(new MergeResult { Merged = false, Success = false }));

            Assert.Single(result.PendingMerges);
            Assert.Contains("alive001", result.PendingMerges);
            Assert.DoesNotContain("gone0002", result.PendingMerges);
        }

        /// <summary>
        /// FALSIFIABILITY ON THE MUTATING PATH. When the branch listing fails (here: a
        /// path that is not a git repo at all), the records must be left for the next
        /// sweep — never merged, never flagged — and the failure must be VISIBLE.
        ///
        /// This is strictly better than the behaviour it replaces: the old per-record
        /// BranchExistsAsync returned false on a git failure, so "couldn't look" was
        /// silently indistinguishable from "branch gone" and left no trace at all.
        /// </summary>
        [Fact]
        public async Task Pass2_BranchListingFailure_SkipsRecordsVisibly_NeverActsOnThem()
        {
            string notARepo = Path.Combine(Path.GetTempPath(), $"mt_notarepo_{Guid.NewGuid():N}");
            Directory.CreateDirectory(notARepo);
            try
            {
                SavePrunedDoneRow(taskId: "aaaa1111", branchName: "task/aaaa1111");
                SavePrunedDoneRow(taskId: "bbbb2222", branchName: "task/bbbb2222");

                int mergeAttempts = 0;
                var janitor = new WorktreeJanitorService(_db);

                var result = await janitor.SweepAsync(
                    getProjectPathForTask: _ => notARepo,
                    recordActivity: (_, _, _) => true,
                    tryMergeForTask: (_, _) =>
                    {
                        mergeAttempts++;
                        return Task.FromResult(new MergeResult { Merged = false, Success = false });
                    });

                Assert.Equal(0, mergeAttempts);
                Assert.Empty(result.PendingMerges);
                Assert.Contains(result.Errors, e => e.Contains("Pass 2 branch listing", StringComparison.Ordinal));

                // One listing attempt per repo, not one per record — the whole point of
                // batching. Both records share a repo, so exactly one error is recorded.
                Assert.Single(result.Errors, e => e.Contains("Pass 2 branch listing", StringComparison.Ordinal));
            }
            finally
            {
                TryDeleteDir(notARepo);
            }
        }

        /// <summary>
        /// PIPELINE RUN 1, debugger LOW — the stale-cache regression the eviction closes.
        ///
        /// The seen-dedup keys on (taskId, branchName), but the MUTATION keys on taskId
        /// alone: WorktreeMergeService derives CanonicalBranch(taskId) whichever record
        /// triggered it. So a helper row (task/&lt;id&gt;--&lt;agent&gt;) and the canonical row
        /// (task/&lt;id&gt;) get DISTINCT dedup keys, both are visited, and the helper's merge
        /// deletes the CANONICAL branch — which the cached set still lists. When the second
        /// merge attempt then fails (detached HEAD, wrong trunk, …), Pass 2 emitted a false
        /// "Branch X still alive for done task Y" for a branch THIS SWEEP had just deleted.
        /// The pre-batching per-record probe returned false there and skipped, so this was a
        /// regression introduced by the batching, not a pre-existing gap.
        ///
        /// Without the eviction this fails with a spurious PendingMerges entry.
        /// </summary>
        [Fact]
        public async Task Pass2_MergeDeletingTheCanonicalBranch_EvictsItSoNoFalsePendingMergeIsEmitted()
        {
            RunGit(_repoRoot, "branch", CanonicalBranch);
            RunGit(_repoRoot, "branch", "task/feed1234--bob");

            // ListPrunedWorktreesForDoneTasks is ORDER BY created_at DESC, so the row saved
            // LAST is visited FIRST. The helper must be visited first — its merge is what
            // deletes the CANONICAL branch — so the canonical row is saved first here.
            // Getting this backwards makes the test pass vacuously (verified: with the rows
            // the other way round it passed even with the eviction removed).
            SavePrunedDoneRow();
            System.Threading.Thread.Sleep(15); // distinct created_at — the ordering is the fixture
            SaveExtraPrunedAgentRow("Bob", branchName: "task/feed1234--bob");

            int mergeCalls = 0;
            var janitor = new WorktreeJanitorService(_db);
            var flagged = new List<string>();

            var result = await janitor.SweepAsync(
                getProjectPathForTask: _ => _repoRoot,
                recordActivity: (action, content, relatedId) =>
                {
                    if (action == "janitor_pending_merge") flagged.Add(content);
                    return true;
                },
                tryMergeForTask: (taskId, _) =>
                {
                    // First call succeeds and deletes the canonical branch (no Stderr, so
                    // this is the clean "merged and deleted" path). Every later call fails
                    // the way a wrong-trunk / detached-HEAD guard would.
                    if (Interlocked.Increment(ref mergeCalls) == 1)
                    {
                        RunGit(_repoRoot, "branch", "-D", CanonicalBranch);
                        return Task.FromResult(new MergeResult { Merged = true, Success = true, MergedInto = "master" });
                    }

                    return Task.FromResult(new MergeResult { Merged = false, Success = false, Stderr = "refusing: main checkout is on the wrong branch" });
                });

            // Assert on the BRANCH NAMED in the message, not on PendingMerges: both rows
            // carry the same taskId, so a legitimate helper-branch pending merge and a
            // false canonical one are indistinguishable by id. And a substring test would
            // false-positive, because "task/feed1234--bob" contains "task/feed1234" — so
            // match the exact rendered phrase.
            Assert.DoesNotContain(
                flagged,
                c => c.Contains($"Branch {CanonicalBranch} still alive", StringComparison.Ordinal));
        }

        /// <summary>
        /// A branch outside the refs/heads/task/ namespace the batch listing covers
        /// still gets its per-record probe, so batching cannot silently stop seeing it.
        /// </summary>
        [Fact]
        public async Task Pass2_NonTaskBranchName_StillProbedPerRecord()
        {
            RunGit(_repoRoot, "branch", "hotfix/feed1234");
            SavePrunedDoneRow(branchName: "hotfix/feed1234");

            var janitor = new WorktreeJanitorService(_db);
            var result = await janitor.SweepAsync(
                getProjectPathForTask: _ => _repoRoot,
                recordActivity: (_, _, _) => true,
                tryMergeForTask: (_, _) => Task.FromResult(new MergeResult { Merged = false, Success = false }));

            Assert.Single(result.PendingMerges);
            Assert.Contains(TaskId, result.PendingMerges);
        }

        // ---- helpers ------------------------------------------------------

        /// <summary>
        /// A second (or third) pruned worktree row for the SAME task and branch under a
        /// different agent — the per-agent-isolation shape that makes duplicate records
        /// real rather than hypothetical.
        /// </summary>
        private void SaveExtraPrunedAgentRow(string agentName, string taskId = TaskId, string branchName = CanonicalBranch)
        {
            _db.SaveWorktreeRecord(
                taskId,
                agentName: agentName,
                worktreePath: Path.Combine(_repoRoot, ".claude", "worktrees", $"{taskId}-{agentName}"),
                branchName: branchName,
                isCanonical: false);
            _db.MarkWorktreePruned(taskId, agentName);
        }

        private void SavePrunedDoneRow(string taskId = TaskId, string branchName = CanonicalBranch)
        {
            _db.SaveTask(new KanbanTask { Id = taskId, Title = "Feed fix", Status = "done", CreatedAt = DateTime.UtcNow });
            _db.SaveWorktreeRecord(
                taskId,
                agentName: "Alice",
                worktreePath: Path.Combine(_repoRoot, ".claude", "worktrees", taskId),
                branchName: branchName,
                isCanonical: true);
            _db.MarkWorktreePruned(taskId, "Alice");
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

        /// <summary>Runs git, asserting exit 0, returning stdout.</summary>
        private static string RunGit(string workingDir, params string[] args)
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
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, $"git {string.Join(' ', args)} failed (exit {proc.ExitCode}) in {workingDir}");
            return stdout;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
