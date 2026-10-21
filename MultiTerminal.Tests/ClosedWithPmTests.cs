using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 7d20e94c item 8: closing a PM pane's tab can close its helpers too, ending them
    /// <c>closed_with_pm</c>, which must stay resumable with the PM. Pins (1) which helper panes the
    /// tracker offers to close, (2) that <c>closed_with_pm</c> is written first and wins over the
    /// <c>pane_closed</c> that follows, (3) that <see cref="PaneLaunchRecorder.GetOpenHelpers"/> keeps such a
    /// helper and a later CLOSED launch of the same session still excludes it, and (4) that a helper
    /// closed with its PM seconds ago is not flagged "may be open elsewhere" and IS resumed.
    /// </summary>
    public sealed class ClosedWithPmTests : IDisposable
    {
        private const string Pm = "pm-session";
        private const string Hs = "11111111-2222-3333-4444-555555555555";
        private const string Root = @"C:\proj";
        private static readonly DateTime T0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _testDbPath;
        private DateTime _now = T0;

        public ClosedWithPmTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_closedwithpm_{Guid.NewGuid():N}.db");
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

        // ---- the tracker: which helper panes a PM's tab close offers to close ----

        /// <summary>Records every call; a session change inside a launch continues it on a new id.</summary>
        private sealed class Sink : IPaneLaunchSink
        {
            private int _next;
            public readonly List<string> Calls = new List<string>();

            public string BeginLaunch(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId)
            {
                string id = "L" + (++_next);
                Calls.Add($"begin {docId} -> {id}");
                return id;
            }

            public string AdoptSession(string launchId, string sessionId, bool sessionChangedInLaunch)
            {
                if (!sessionChangedInLaunch) return launchId;
                string id = "L" + (++_next);
                Calls.Add($"continue {launchId} -> {id}");
                return id;
            }

            public void LinkSpawner(string childLaunchId, string spawnerLaunchId) => Calls.Add($"link {childLaunchId} <- {spawnerLaunchId}");
            public void End(string launchId, string reason) => Calls.Add($"end {launchId} {reason}");
            public void EndAllForShutdown() => Calls.Add("end-all");
        }

        private static void Begin(PaneLaunchTracker t, string docId) => t.OnLaunchBegan(docId, "n-" + docId, "p", @"C:\w", "claude", null);

        [Fact]
        public void A_pm_offers_exactly_the_open_helpers_its_current_launch_spawned_linked_in_either_order()
        {
            var t = new PaneLaunchTracker(new Sink());
            Begin(t, "PM");
            Begin(t, "OTHER");
            Begin(t, "A");
            t.LinkSpawner("A", "PM");     // the helper's row existed first
            t.LinkSpawner("B", "PM");     // the link came first (pending) ...
            Begin(t, "B");                // ... and is applied when B's launch begins
            Begin(t, "C");
            t.LinkSpawner("C", "OTHER");  // another PM's helper
            Begin(t, "D");                // a pane nobody spawned

            Assert.Equal(new[] { "A", "B" }, t.GetOpenHelperDocIds("PM").OrderBy(d => d, StringComparer.Ordinal));
            Assert.Equal(new[] { "C" }, t.GetOpenHelperDocIds("OTHER"));
            Assert.Empty(t.GetOpenHelperDocIds("D"));
        }

        [Fact]
        public void A_helper_whose_process_exited_or_whose_pane_closed_is_not_offered()
        {
            var t = new PaneLaunchTracker(new Sink());
            Begin(t, "PM");
            Begin(t, "A");
            Begin(t, "B");
            t.LinkSpawner("A", "PM");
            t.LinkSpawner("B", "PM");

            t.OnProcessExited("A");
            t.OnPaneClosed("B");

            Assert.Empty(t.GetOpenHelperDocIds("PM"));
        }

        [Fact]
        public void A_pm_whose_own_process_exited_still_offers_its_running_helpers_until_it_relaunches()
        {
            var t = new PaneLaunchTracker(new Sink());
            Begin(t, "PM");
            Begin(t, "A");
            t.LinkSpawner("A", "PM");

            t.OnProcessExited("PM"); // Home, or claude ended; the pane stays open
            Assert.Equal(new[] { "A" }, t.GetOpenHelperDocIds("PM"));

            Begin(t, "PM");          // a new session in the same pane: A belongs to the old one
            Assert.Empty(t.GetOpenHelperDocIds("PM"));
        }

        [Fact]
        public void Helpers_spawned_before_a_clear_belong_to_the_old_session_and_after_it_to_the_new()
        {
            var t = new PaneLaunchTracker(new Sink());
            Begin(t, "PM");
            t.OnSessionAdopted("PM", "s1");
            Begin(t, "OLD");
            t.LinkSpawner("OLD", "PM");

            t.OnSessionAdopted("PM", "s2", previousSessionId: "s1"); // /clear: the PM continues on a new launch
            Begin(t, "NEW");
            t.LinkSpawner("NEW", "PM");

            Assert.Equal(new[] { "NEW" }, t.GetOpenHelperDocIds("PM"));
        }

        [Fact]
        public void A_helpers_own_clear_keeps_it_linked_to_its_pm()
        {
            var t = new PaneLaunchTracker(new Sink());
            Begin(t, "PM");
            Begin(t, "A");
            t.LinkSpawner("A", "PM");
            t.OnSessionAdopted("A", "h1");
            t.OnSessionAdopted("A", "h2", previousSessionId: "h1"); // continuation row, same spawner link

            Assert.Equal(new[] { "A" }, t.GetOpenHelperDocIds("PM"));
        }

        [Fact]
        public void Nothing_is_offered_once_MT_is_shutting_down()
        {
            var t = new PaneLaunchTracker(new Sink());
            Begin(t, "PM");
            Begin(t, "A");
            t.LinkSpawner("A", "PM");
            t.BeginShutdown();
            Assert.Empty(t.GetOpenHelperDocIds("PM"));
        }

        [Fact]
        public void Closed_with_pm_is_written_first_and_the_following_pane_closed_writes_nothing()
        {
            var sink = new Sink();
            var t = new PaneLaunchTracker(sink);
            Begin(t, "PM");
            Begin(t, "A");
            t.LinkSpawner("A", "PM");

            t.OnHelperClosedWithPm("A");
            t.OnPaneClosed("A");   // the helper's Close raises this
            t.OnPaneClosed("PM");  // then the PM closes as it always did

            Assert.Equal(
                new[] { "begin PM -> L1", "begin A -> L2", "link L2 <- L1", "end L2 closed_with_pm", "end L1 pane_closed" },
                sink.Calls);
            Assert.Empty(t.GetOpenHelperDocIds("PM"));
        }

        [Fact]
        public void The_question_names_the_pm_and_its_helpers()
        {
            Assert.StartsWith(
                "Also close Alice's 2 helpers (TestA, TestB)? They'll come back when you resume Alice.",
                HelperCloseWithPm.Question("Alice", new[] { "TestA", "TestB" }));
            var one = HelperCloseWithPm.Question("Alice", new[] { "TestA" });
            Assert.StartsWith("Also close Alice's helper TestA? It'll come back when you resume Alice.", one);
            Assert.Contains("Cancel: keep Alice open.", one);
        }

        // ---- the record: closed_with_pm is resumable ----

        private PaneLaunchRecorder Recorder(TaskDatabase db, string runId = "run-1")
            => new PaneLaunchRecorder(db, null, runId, () => _now);

        private string Launch(PaneLaunchRecorder rec, string name, string sessionId, string spawnerLaunchId = null)
        {
            _now = _now.AddMinutes(1);
            var id = rec.BeginLaunch("doc-" + name, name, "proj", Root, "claude", null);
            Assert.NotNull(id);
            rec.AdoptSession(id, sessionId);
            if (spawnerLaunchId != null) rec.LinkSpawner(id, spawnerLaunchId);
            return id;
        }

        [Fact]
        public void A_helper_closed_with_its_pm_is_offered_when_the_pm_is_resumed()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Alice", Pm);
            var h = Launch(rec, "TestA", Hs, pm);

            rec.End(h, PaneLaunchEndReason.ClosedWithPm);
            rec.End(h, PaneLaunchEndReason.PaneClosed); // the close that follows: the first end wins
            rec.End(pm, PaneLaunchEndReason.PaneClosed);

            Assert.Equal(new[] { Hs }, rec.GetOpenHelpers(Pm).Select(x => x.SessionId));
        }

        [Fact]
        public void A_later_closed_launch_of_the_same_helper_session_still_excludes_it()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Alice", Pm);
            var h = Launch(rec, "TestA", Hs, pm);
            rec.End(h, PaneLaunchEndReason.ClosedWithPm);

            // The helper session was resumed by someone else afterwards and that pane was closed by the Owner.
            var later = Launch(rec, "TestA", Hs);
            rec.End(later, PaneLaunchEndReason.PaneClosed);

            Assert.Empty(rec.GetOpenHelpers(Pm));
        }

        [Fact]
        public void A_later_closed_with_pm_launch_does_not_exclude_it()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Alice", Pm);
            var h = Launch(rec, "TestA", Hs, pm);
            rec.End(h, PaneLaunchEndReason.MtExit);

            var later = Launch(rec, "TestA", Hs);
            rec.End(later, PaneLaunchEndReason.ClosedWithPm);

            Assert.Equal(new[] { Hs }, rec.GetOpenHelpers(Pm).Select(x => x.SessionId));
        }

        private static PaneLaunchRecord Row(string run, int seq, string endReason) => new PaneLaunchRecord
        {
            Seq = seq,
            MtRunId = run,
            SessionId = Hs,
            StartedAt = T0.AddMinutes(seq),
            EndedAt = endReason == null ? (DateTime?)null : T0.AddMinutes(seq).AddSeconds(30),
            EndReason = endReason,
        };

        [Theory]
        [InlineData("now")]   // closed with its PM in this run: no process runs it, so NOT OpenInCurrentRun
        [InlineData("old")]   // closed with its PM in an earlier run
        public void Closed_with_pm_classifies_as_closed_with_pm_in_any_run(string run)
        {
            Assert.Equal(PriorLaunchState.ClosedWithPm,
                PaneLaunchRecorder.ClassifyPriorLaunch(new[] { Row(run, 1, PaneLaunchEndReason.ClosedWithPm) }, "now", _ => false));
        }

        [Fact]
        public void Only_the_newest_row_decides_so_a_later_owner_close_is_ended()
        {
            Assert.Equal(PriorLaunchState.Ended, PaneLaunchRecorder.ClassifyPriorLaunch(
                new[] { Row("now", 1, PaneLaunchEndReason.ClosedWithPm), Row("now", 2, PaneLaunchEndReason.PaneClosed) }, "now", _ => false));
        }

        private static SessionCatalogEntry WrittenAt(DateTime lastWrite) => new SessionCatalogEntry
        {
            SessionId = Hs,
            Cwd = Root,
            TranscriptPath = @"C:\t\" + Hs + ".jsonl",
            LastActivityUtc = lastWrite,
            Status = SessionCatalogStatus.PossiblyOpenElsewhere, // written within the recency window
        };

        [Fact]
        public void A_helper_closed_with_its_pm_five_seconds_ago_is_resumed()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Alice", Pm);
            var h = Launch(rec, "TestA", Hs, pm);
            var lastWrite = _now.AddSeconds(55);
            _now = lastWrite.AddSeconds(5);
            rec.End(h, PaneLaunchEndReason.ClosedWithPm);

            var prior = rec.GetPriorLaunchState(Hs);
            Assert.Equal(PriorLaunchState.ClosedWithPm, prior);

            var helper = Assert.Single(rec.GetOpenHelpers(Pm));
            var check = HelperResume.Check(helper, WrittenAt(lastWrite), Array.Empty<PaneSessionState>(), Root, _ => lastWrite, _ => true, prior);
            Assert.True(check.CanResume, check.SkipReason);
        }

        [Fact]
        public void The_same_recent_helper_without_the_record_is_still_skipped()
        {
            // The control for the fact above: same transcript, no launch-record waiver.
            var helper = new OpenHelper { SessionId = Hs, TerminalName = "TestA", Kind = "claude", Cwd = Root };
            var check = HelperResume.Check(helper, WrittenAt(T0), Array.Empty<PaneSessionState>(), Root, _ => T0, _ => true, PriorLaunchState.Ended);
            Assert.Contains("may still be open elsewhere", check.SkipReason);
        }

        [Fact]
        public void The_waiver_does_not_override_this_runs_live_set()
        {
            var helper = new OpenHelper { SessionId = Hs, TerminalName = "TestA", Kind = "claude", Cwd = Root };
            var holder = new PaneSessionState(true, Hs, T0.AddHours(-1));
            var check = HelperResume.Check(helper, WrittenAt(T0), new[] { holder }, Root, _ => T0, _ => true, PriorLaunchState.ClosedWithPm);
            Assert.False(check.CanResume);
        }
    }
}
