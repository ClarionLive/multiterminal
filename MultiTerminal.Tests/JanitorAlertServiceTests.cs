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

        // ---- helpers ------------------------------------------------------

        private void SavePrunedDoneRow()
        {
            _db.SaveTask(new KanbanTask { Id = TaskId, Title = "Feed fix", Status = "done", CreatedAt = DateTime.UtcNow });
            _db.SaveWorktreeRecord(
                TaskId,
                agentName: "Alice",
                worktreePath: Path.Combine(_repoRoot, ".claude", "worktrees", TaskId),
                branchName: CanonicalBranch,
                isCanonical: true);
            _db.MarkWorktreePruned(TaskId, "Alice");
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
