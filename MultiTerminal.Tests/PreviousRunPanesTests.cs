using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 3e1c09b8: which Claude panes the previous MT run still had open when it ended, offered for
    /// restore at startup. The pure selector over one run's rows, and the recorder's choice of WHICH run
    /// (the one just before this one, read only when its process is provably gone).
    /// </summary>
    public sealed class PreviousRunPanesTests : IDisposable
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);
        private const string S1 = "11111111-0000-0000-0000-000000000001";
        private const string S2 = "11111111-0000-0000-0000-000000000002";
        private const string S3 = "11111111-0000-0000-0000-000000000003";

        private readonly string _testDbPath;

        public PreviousRunPanesTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_prevrun_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
        }

        private static long _seq;

        private static PaneLaunchRecord Row(string doc, string session, string endReason = null, string kind = PaneLaunchKind.Claude,
            string spawner = null, string id = null, bool ended = false) => new PaneLaunchRecord
        {
            Id = id ?? Guid.NewGuid().ToString(),
            MtRunId = "old",
            DocId = doc,
            TerminalName = "N-" + doc,
            ProjectId = "p",
            Cwd = @"C:\proj",
            Kind = kind,
            SessionId = session,
            SpawnerLaunchId = spawner,
            StartedAt = T0,
            EndedAt = ended || endReason != null ? T0.AddMinutes(1) : (DateTime?)null,
            EndReason = endReason,
            Seq = ++_seq,
        };

        // ---- the pure selector ----

        [Theory]
        [InlineData(null)]                              // MT crashed with it open
        [InlineData(PaneLaunchEndReason.MtExit)]        // MT exited with it open
        public void A_pane_open_at_the_end_is_offered(string reason)
        {
            var panes = PaneLaunchRecorder.SelectRestorable(new[] { Row("a", S1, reason) });
            Assert.Equal(S1, Assert.Single(panes).SessionId);
        }

        [Theory]
        [InlineData(PaneLaunchEndReason.PaneClosed)]
        [InlineData(PaneLaunchEndReason.CloseHelper)]
        [InlineData(PaneLaunchEndReason.ProcessExit)]
        [InlineData(PaneLaunchEndReason.ClosedWithPm)]
        [InlineData(PaneLaunchEndReason.SessionChanged)]
        public void A_pane_closed_before_the_end_is_not_offered(string reason)
        {
            Assert.Empty(PaneLaunchRecorder.SelectRestorable(new[] { Row("a", S1, reason) }));
        }

        [Theory]
        [InlineData(PaneLaunchKind.PowerShell)]
        [InlineData(PaneLaunchKind.Codex)]
        [InlineData(PaneLaunchKind.Unknown)]
        public void Only_claude_panes_are_offered(string kind)
        {
            Assert.Empty(PaneLaunchRecorder.SelectRestorable(new[] { Row("a", S1, kind: kind) }));
        }

        [Fact]
        public void A_pane_with_no_session_is_not_offered()
        {
            Assert.Empty(PaneLaunchRecorder.SelectRestorable(new[] { Row("a", null) }));
        }

        [Fact]
        public void Only_the_panes_newest_launch_counts()
        {
            // The pane's later launch was closed, so the earlier one (even one marked mt_exit) is not offered.
            var older = Row("a", S1, PaneLaunchEndReason.MtExit);
            var newerClosed = Row("a", S2, PaneLaunchEndReason.PaneClosed);
            Assert.Empty(PaneLaunchRecorder.SelectRestorable(new[] { older, newerClosed }));

            var newerOpen = Row("b", S3, PaneLaunchEndReason.MtExit);
            var olderOpen = Row("b", S1);
            olderOpen.Seq = newerOpen.Seq - 10;
            Assert.Equal(S3, Assert.Single(PaneLaunchRecorder.SelectRestorable(new[] { olderOpen, newerOpen })).SessionId);
        }

        [Fact]
        public void A_session_is_offered_once()
        {
            var panes = PaneLaunchRecorder.SelectRestorable(new[] { Row("a", S1), Row("b", S1.ToUpperInvariant()) });
            Assert.Equal("b", Assert.Single(panes).DocId);
        }

        [Fact]
        public void A_helper_is_nested_under_its_spawner_pane_even_after_the_pm_cleared()
        {
            var pmFirst = Row("pm", S1, PaneLaunchEndReason.SessionChanged);
            var helper = Row("h", S2, PaneLaunchEndReason.MtExit, spawner: pmFirst.Id);
            var pmContinuation = Row("pm", S3, PaneLaunchEndReason.MtExit);

            var panes = PaneLaunchRecorder.SelectRestorable(new[] { pmFirst, helper, pmContinuation });

            var pm = panes.Single(p => p.DocId == "pm");
            Assert.Equal(S3, pm.SessionId);
            Assert.Null(pm.ParentLaunchId);
            Assert.Equal(pmContinuation.Id, panes.Single(p => p.DocId == "h").ParentLaunchId);
        }

        [Fact]
        public void A_helper_whose_spawner_is_not_offered_is_top_level()
        {
            var pm = Row("pm", S1, PaneLaunchEndReason.PaneClosed);
            var helper = Row("h", S2, spawner: pm.Id);
            Assert.Null(Assert.Single(PaneLaunchRecorder.SelectRestorable(new[] { pm, helper })).ParentLaunchId);

            var orphan = Row("o", S3, spawner: "no-such-row");
            Assert.Null(Assert.Single(PaneLaunchRecorder.SelectRestorable(new[] { orphan })).ParentLaunchId);
        }

        [Fact]
        public void A_helper_of_a_helper_comes_back_on_its_own()
        {
            var pm = Row("pm", S1);
            var helper = Row("h", S2, spawner: pm.Id);
            var grandchild = Row("g", S3, spawner: helper.Id);

            var panes = PaneLaunchRecorder.SelectRestorable(new[] { pm, helper, grandchild });

            Assert.Equal(pm.Id, panes.Single(p => p.DocId == "h").ParentLaunchId);
            Assert.Null(panes.Single(p => p.DocId == "g").ParentLaunchId);
        }

        [Fact]
        public void Panes_come_back_in_launch_order()
        {
            var a = Row("a", S1);
            var b = Row("b", S2);
            var c = Row("c", S3);
            Assert.Equal(new[] { "a", "b", "c" }, PaneLaunchRecorder.SelectRestorable(new[] { c, a, b }).Select(p => p.DocId));
        }

        // ---- which run ----

        private static readonly MtProcessIdentity OldProcess = new MtProcessIdentity(4242, T0.AddHours(-2));

        private static PaneLaunchRecorder Run(TaskDatabase db, string runId, int minute, MtProcessIdentity process, Func<MtProcessIdentity, MtProcessLiveness> probe = null)
            => new PaneLaunchRecorder(db, null, runId, () => T0.AddMinutes(minute), process, probe);

        private static void OpenPane(PaneLaunchRecorder run, string doc, string session)
        {
            var id = run.BeginLaunch(doc, "N-" + doc, "p", @"C:\proj", PaneLaunchKind.Claude, null);
            run.AdoptSession(id, session);
        }

        [Fact]
        public void The_previous_runs_open_panes_are_offered_when_its_process_is_gone()
        {
            using var db = new TaskDatabase();
            var previous = Run(db, "run-old", 0, OldProcess);
            OpenPane(previous, "a", S1);
            OpenPane(previous, "b", S2);
            previous.EndAllForShutdown();

            var current = Run(db, "run-new", 10, new MtProcessIdentity(5555, T0.AddMinutes(10)), _ => MtProcessLiveness.Gone);
            Assert.Equal(new[] { S1, S2 }, current.GetPreviousRunOpenPanes().Select(p => p.SessionId));
        }

        [Theory]
        [InlineData(MtProcessLiveness.Alive)]
        [InlineData(MtProcessLiveness.Unknown)]
        public void Nothing_is_offered_when_the_previous_process_may_still_run(MtProcessLiveness liveness)
        {
            using var db = new TaskDatabase();
            var previous = Run(db, "run-old", 0, OldProcess);
            OpenPane(previous, "a", S1);

            var current = Run(db, "run-new", 10, new MtProcessIdentity(5555, T0.AddMinutes(10)), _ => liveness);
            Assert.Empty(current.GetPreviousRunOpenPanes());
        }

        [Fact]
        public void Only_the_run_just_before_this_one_counts()
        {
            // run-old had panes; run-mid restored nothing and opened nothing. The next start offers nothing.
            using var db = new TaskDatabase();
            var old = Run(db, "run-old", 0, OldProcess);
            OpenPane(old, "a", S1);
            old.EndAllForShutdown();
            Run(db, "run-mid", 5, new MtProcessIdentity(4343, T0.AddMinutes(5)));

            var current = Run(db, "run-new", 10, new MtProcessIdentity(5555, T0.AddMinutes(10)), _ => MtProcessLiveness.Gone);
            Assert.Empty(current.GetPreviousRunOpenPanes());
        }

        [Fact]
        public void The_first_run_has_nothing_to_offer()
        {
            using var db = new TaskDatabase();
            var current = Run(db, "run-new", 10, new MtProcessIdentity(5555, T0.AddMinutes(10)), _ => MtProcessLiveness.Gone);
            Assert.Empty(current.GetPreviousRunOpenPanes());
        }
    }
}
