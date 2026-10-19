using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pipeline Run 1 fixes for task 7d20e94c, through the REAL <see cref="TaskDatabase"/> on a temp
    /// SQLite file where a row is involved:
    /// <list type="bullet">
    /// <item>Fail-closed previous-run classification: a previous run's open launch is "died with that
    /// run" only when its MT process is provably gone (injected liveness probe).</item>
    /// <item><c>seq</c> decides launch order, so identical start times cannot reorder launches.</item>
    /// <item>A late adoption of an earlier launch is ignored by the tracker.</item>
    /// <item>A resumed launch carries its session from the start.</item>
    /// <item>A session change inside one launch (<c>/clear</c>) continues the launch on a new row and
    /// leaves the old session's helpers with it.</item>
    /// <item>The launch kind of a real Claude command, and the shared helper-name rule.</item>
    /// </list>
    /// Not covered here (MainForm wiring, no seam): the "Launch as" refusal now calling
    /// <c>ReturnToStartScreen</c>, the posted-event drop on <c>CurrentClaudeSessionId</c>, and which
    /// launch sites pass an explicit kind.
    /// </summary>
    public sealed class PaneLaunchRun1Tests : IDisposable
    {
        private const string Pm = "pm-session";
        private static readonly DateTime T0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _testDbPath;

        public PaneLaunchRun1Tests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_run1_{Guid.NewGuid():N}.db");
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

        // ---- 1. fail-closed previous-run classification ----

        private static PaneLaunchRecord OpenRowOf(string run) => new PaneLaunchRecord
        {
            Seq = 1,
            MtRunId = run,
            SessionId = "s",
            StartedAt = T0,
        };

        [Fact]
        public void A_previous_runs_open_launch_died_with_it_only_when_that_run_is_provably_gone()
        {
            var rows = new[] { OpenRowOf("old") };
            Assert.Equal(PriorLaunchState.DiedWithPreviousRun, PaneLaunchRecorder.ClassifyPriorLaunch(rows, "now", _ => true));
            Assert.Equal(PriorLaunchState.OpenElsewhere, PaneLaunchRecorder.ClassifyPriorLaunch(rows, "now", _ => false));
            Assert.Equal(PriorLaunchState.OpenElsewhere, PaneLaunchRecorder.ClassifyPriorLaunch(rows, "now", null));
            Assert.Equal(PriorLaunchState.OpenElsewhere, PaneLaunchRecorder.ClassifyPriorLaunch(rows, "now", _ => throw new InvalidOperationException()));
        }

        [Theory]
        [InlineData(MtProcessLiveness.Gone, PriorLaunchState.DiedWithPreviousRun)]
        [InlineData(MtProcessLiveness.Alive, PriorLaunchState.OpenElsewhere)]
        [InlineData(MtProcessLiveness.Unknown, PriorLaunchState.OpenElsewhere)]
        public void The_recorder_asks_the_probe_about_the_process_the_previous_run_recorded(MtProcessLiveness answer, PriorLaunchState expected)
        {
            using var db = new TaskDatabase();
            var oldProcess = new MtProcessIdentity(31337, T0.AddHours(-2));
            var previous = new PaneLaunchRecorder(db, null, "run-old", () => T0, oldProcess, null);
            var id = previous.BeginLaunch("doc", "Notes", "p", @"C:\p", "claude", null);
            previous.AdoptSession(id, "s1");
            previous.EndAllForShutdown();

            var asked = new List<MtProcessIdentity>();
            var current = new PaneLaunchRecorder(db, null, "run-new", () => T0.AddMinutes(5), null, p => { asked.Add(p); return answer; });

            Assert.Equal(expected, current.GetPriorLaunchState("s1"));
            var probed = Assert.Single(asked);
            Assert.Equal(31337, probed.ProcessId);
            Assert.Equal(oldProcess.StartedAtUtc, probed.StartedAtUtc);
        }

        [Fact]
        public void A_previous_run_that_recorded_no_process_is_never_provably_gone()
        {
            using var db = new TaskDatabase();
            var previous = new PaneLaunchRecorder(db, null, "run-old", () => T0); // records no process
            var id = previous.BeginLaunch("doc", "Notes", "p", @"C:\p", "claude", null);
            previous.AdoptSession(id, "s1");

            bool probed = false;
            var current = new PaneLaunchRecorder(db, null, "run-new", () => T0.AddMinutes(5), null, _ => { probed = true; return MtProcessLiveness.Gone; });
            Assert.Equal(PriorLaunchState.OpenElsewhere, current.GetPriorLaunchState("s1"));
            Assert.False(probed);
        }

        [Fact]
        public void The_real_probe_tells_this_process_from_a_reused_id_and_an_exited_process()
        {
            var self = MtProcessProbe.Current();
            Assert.NotNull(self);
            Assert.Equal(MtProcessLiveness.Alive, MtProcessProbe.Probe(self));

            // Same id, another start time: the id was reused by another process.
            Assert.Equal(MtProcessLiveness.Gone, MtProcessProbe.Probe(new MtProcessIdentity(self.ProcessId, self.StartedAtUtc.AddMinutes(-30))));

            // A process that has exited.
            var psi = new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false };
            MtProcessIdentity exited;
            using (var child = Process.Start(psi))
            {
                exited = new MtProcessIdentity(child.Id, child.StartTime.ToUniversalTime());
                Assert.True(child.WaitForExit(10000));
            }
            Assert.Equal(MtProcessLiveness.Gone, MtProcessProbe.Probe(exited));
        }

        [Fact]
        public void A_helper_left_open_by_a_run_not_provably_gone_is_skipped_even_with_a_quiet_transcript()
        {
            const string sid = "11111111-2222-3333-4444-555555555555";
            var quiet = new SessionCatalogEntry
            {
                SessionId = sid,
                Cwd = @"C:\proj",
                TranscriptPath = @"C:\t\" + sid + ".jsonl",
                LastActivityUtc = T0.AddDays(-1),
                Status = SessionCatalogStatus.Available,
            };
            var check = HelperResume.Check(
                new OpenHelper { SessionId = sid, TerminalName = "Notes", Kind = "claude", Cwd = @"C:\proj" },
                quiet, null, @"C:\proj", _ => T0.AddDays(-1), _ => true, PriorLaunchState.OpenElsewhere);
            Assert.False(check.CanResume);
            Assert.Contains("may still be open elsewhere", check.SkipReason);
        }

        // ---- 2. seq, not start time, orders launches ----

        [Fact]
        public void Seq_is_assigned_in_insert_order_even_when_start_times_are_identical()
        {
            using var db = new TaskDatabase();
            var rec = new PaneLaunchRecorder(db, null, "run", () => T0);
            var a = rec.BeginLaunch("d", "n", "p", "c", "claude", "s");
            var b = rec.BeginLaunch("d", "n", "p", "c", "claude", "s");
            var c = rec.BeginLaunch("d", "n", "p", "c", "claude", "s");

            Assert.Equal(new[] { c, b, a }, db.GetPaneLaunchesBySession("s").Select(r => r.Id).ToArray());
            var seqs = new[] { a, b, c }.Select(id => db.GetPaneLaunch(id).Seq).ToArray();
            Assert.True(seqs[0] < seqs[1] && seqs[1] < seqs[2], string.Join(",", seqs));
        }

        [Fact]
        public void A_later_closed_launch_with_the_same_start_time_still_closes_the_helper()
        {
            using var db = new TaskDatabase();
            var rec = new PaneLaunchRecorder(db, null, "run", () => T0); // every row starts at T0
            var pm = rec.BeginLaunch("doc-pm", "Pat", "p", "c", "claude", null);
            rec.AdoptSession(pm, Pm);
            var helper = rec.BeginLaunch("doc-h", "Helper1", "p", "c", "claude", null);
            rec.AdoptSession(helper, "h1");
            rec.LinkSpawner(helper, pm);

            // Someone else resumed h1 later (same timestamp) and closed it.
            var later = rec.BeginLaunch("doc-x", "Helper1", "p", "c", "claude", "h1");
            rec.End(later, PaneLaunchEndReason.PaneClosed);

            Assert.Empty(rec.GetOpenHelpers(Pm));
        }

        [Fact]
        public void The_newest_launch_by_seq_decides_the_prior_state_when_start_times_tie()
        {
            // The older launch (seq 1) is listed FIRST: an order by start time alone keeps input order on
            // a tie and would pick it.
            var rows = new[]
            {
                new PaneLaunchRecord { Seq = 1, MtRunId = "now", SessionId = "s", StartedAt = T0, EndedAt = T0, EndReason = PaneLaunchEndReason.PaneClosed },
                new PaneLaunchRecord { Seq = 2, MtRunId = "now", SessionId = "s", StartedAt = T0 },
            };
            Assert.Equal(PriorLaunchState.OpenInCurrentRun, PaneLaunchRecorder.ClassifyPriorLaunch(rows, "now", null));
        }

        [Fact]
        public void The_seq_migration_backfills_existing_rows_from_rowid()
        {
            string a, b;
            using (var first = new TaskDatabase())
            {
                var rec = new PaneLaunchRecorder(first, null, "run", () => T0);
                a = rec.BeginLaunch("d", "n", "p", "c", "claude", "s");
                b = rec.BeginLaunch("d", "n", "p", "c", "claude", "s");
            }
            SQLiteConnection.ClearAllPools();

            using (var raw = new SQLiteConnection($"Data Source={_testDbPath}"))
            {
                raw.Open();
                using var wipe = new SQLiteCommand("UPDATE pane_launches SET seq = NULL", raw);
                Assert.Equal(2, wipe.ExecuteNonQuery());
                using var forget = new SQLiteCommand("DELETE FROM schema_migrations WHERE name = 'MigrateAddPaneLaunchSeqAndMtRuns'", raw);
                Assert.Equal(1, forget.ExecuteNonQuery());
            }
            SQLiteConnection.ClearAllPools();

            using var db = new TaskDatabase();
            long seqA = db.GetPaneLaunch(a).Seq, seqB = db.GetPaneLaunch(b).Seq;
            Assert.True(seqA > 0 && seqB > seqA, $"{seqA},{seqB}");
        }

        // ---- 4. a late adoption of an earlier launch ----

        private sealed class RecordingSink : IPaneLaunchSink
        {
            private int _next;
            public readonly List<string> Calls = new List<string>();
            public string ContinueAs;

            public string BeginLaunch(string docId, string name, string projectId, string cwd, string kind, string resumedFromSessionId)
            {
                string id = "L" + (++_next);
                Calls.Add($"begin {docId} -> {id}");
                return id;
            }

            public string AdoptSession(string launchId, string sessionId, bool sessionChangedInLaunch)
            {
                Calls.Add($"adopt {launchId} {sessionId}{(sessionChangedInLaunch ? " changed" : string.Empty)}");
                return sessionChangedInLaunch && ContinueAs != null ? ContinueAs : launchId;
            }

            public void LinkSpawner(string childLaunchId, string spawnerLaunchId) => Calls.Add($"link {childLaunchId} <- {spawnerLaunchId}");
            public void End(string launchId, string reason) => Calls.Add($"end {launchId} {reason}");
            public void EndAllForShutdown() => Calls.Add("end-all");
        }

        [Fact]
        public void An_adoption_carrying_an_earlier_launch_generation_is_ignored()
        {
            var sink = new RecordingSink();
            var t = new PaneLaunchTracker(sink);
            t.OnLaunchBegan("A", "n", "p", "c", "claude", null, launchGeneration: 1);
            t.OnLaunchBegan("A", "n", "p", "c", "claude", null, launchGeneration: 3);

            t.OnSessionAdopted("A", "old-session", null, launchGeneration: 1);
            Assert.DoesNotContain(sink.Calls, c => c.StartsWith("adopt", StringComparison.Ordinal));

            t.OnSessionAdopted("A", "new-session", null, launchGeneration: 3);
            Assert.Contains("adopt L2 new-session", sink.Calls);
        }

        // ---- 5. a resumed launch carries its session from the start ----

        [Fact]
        public void A_resumed_helper_closed_before_adoption_is_not_resurrected()
        {
            using var db = new TaskDatabase();
            var run1 = new PaneLaunchRecorder(db, null, "run-1", () => T0);
            var pm1 = run1.BeginLaunch("doc-pm", "Pat", "p", "c", "claude", null);
            run1.AdoptSession(pm1, Pm);
            var h1 = run1.BeginLaunch("doc-h", "Helper1", "p", "c", "claude", null);
            run1.AdoptSession(h1, "h1");
            run1.LinkSpawner(h1, pm1);
            run1.EndAllForShutdown();

            var run2 = new PaneLaunchRecorder(db, null, "run-2", () => T0.AddMinutes(5));
            var pm2 = run2.BeginLaunch("doc-pm2", "Pat", "p", "c", "claude", Pm);
            var h2 = run2.BeginLaunch("doc-h2", "Helper1", "p", "c", "claude", "h1");
            run2.LinkSpawner(h2, pm2);
            Assert.Equal("h1", db.GetPaneLaunch(h2).SessionId);

            // Closed before the statusline ever reported its session.
            run2.End(h2, PaneLaunchEndReason.PaneClosed);

            Assert.Empty(run2.GetOpenHelpers(Pm));
        }

        [Fact]
        public void The_first_adoption_replaces_a_prefilled_resume_id_without_a_continuation()
        {
            using var db = new TaskDatabase();
            var rec = new PaneLaunchRecorder(db, null, "run", () => T0);
            var id = rec.BeginLaunch("d", "n", "p", "c", "claude", "resumed");

            Assert.Equal(id, rec.AdoptSession(id, "actual", sessionChangedInLaunch: false));
            var row = db.GetPaneLaunch(id);
            Assert.Equal("actual", row.SessionId);
            Assert.Null(row.EndedAt);
            Assert.Empty(db.GetPaneLaunchesBySession("resumed"));
        }

        // ---- 6. /clear continues the launch on a new row ----

        [Fact]
        public void A_session_change_ends_the_row_session_changed_and_continues_on_a_new_row()
        {
            using var db = new TaskDatabase();
            var rec = new PaneLaunchRecorder(db, null, "run", () => T0);
            var pmSpawner = rec.BeginLaunch("doc-boss", "Boss", "p", "c", "claude", null);
            var pm = rec.BeginLaunch("doc-pm", "Pat", "proj", @"C:\w", "claude", null);
            rec.LinkSpawner(pm, pmSpawner);
            rec.AdoptSession(pm, "S1");
            var helper = rec.BeginLaunch("doc-h", "Helper1", "proj", @"C:\w", "claude", null);
            rec.AdoptSession(helper, "h1");
            rec.LinkSpawner(helper, pm);

            string continuation = rec.AdoptSession(pm, "S2", sessionChangedInLaunch: true);

            Assert.NotNull(continuation);
            Assert.NotEqual(pm, continuation);
            var old = db.GetPaneLaunch(pm);
            Assert.Equal("S1", old.SessionId);
            Assert.Equal(PaneLaunchEndReason.SessionChanged, old.EndReason);
            var next = db.GetPaneLaunch(continuation);
            Assert.Equal("S2", next.SessionId);
            Assert.Null(next.EndedAt);
            Assert.Equal(("run", "doc-pm", "Pat", "proj", @"C:\w", "claude", pmSpawner),
                (next.MtRunId, next.DocId, next.TerminalName, next.ProjectId, next.Cwd, next.Kind, next.SpawnerLaunchId));
            Assert.True(next.Seq > old.Seq);

            // The helper stays with the session that spawned it; the new session has none yet.
            Assert.Equal(new[] { "h1" }, rec.GetOpenHelpers("S1").Select(h => h.SessionId).ToArray());
            Assert.Empty(rec.GetOpenHelpers("S2"));

            // A helper spawned after the change links to the continuation, so it belongs to S2.
            var later = rec.BeginLaunch("doc-h2", "Helper2", "proj", @"C:\w", "claude", null);
            rec.AdoptSession(later, "h2");
            rec.LinkSpawner(later, continuation);
            Assert.Equal(new[] { "h2" }, rec.GetOpenHelpers("S2").Select(h => h.SessionId).ToArray());
            Assert.Equal(new[] { "h1" }, rec.GetOpenHelpers("S1").Select(h => h.SessionId).ToArray());

            // The old session is not running any more, in this run or the next.
            Assert.Equal(PriorLaunchState.Ended, rec.GetPriorLaunchState("S1"));
            Assert.Equal(PriorLaunchState.OpenInCurrentRun, rec.GetPriorLaunchState("S2"));
        }

        [Fact]
        public void A_session_change_on_an_ended_row_writes_nothing()
        {
            using var db = new TaskDatabase();
            var rec = new PaneLaunchRecorder(db, null, "run", () => T0);
            var id = rec.BeginLaunch("d", "n", "p", "c", "claude", null);
            rec.AdoptSession(id, "S1");
            rec.End(id, PaneLaunchEndReason.ProcessExit);

            Assert.Null(rec.AdoptSession(id, "S2", sessionChangedInLaunch: true));
            Assert.Equal("S1", db.GetPaneLaunch(id).SessionId);
            Assert.Empty(db.GetPaneLaunchesBySession("S2"));
        }

        [Fact]
        public void The_tracker_moves_the_pane_to_the_continuation_so_later_spawns_link_to_it()
        {
            var sink = new RecordingSink { ContinueAs = "L-cont" };
            var t = new PaneLaunchTracker(sink);
            t.OnLaunchBegan("PM", "Pat", "p", "c", "claude", null, launchGeneration: 1);
            t.OnSessionAdopted("PM", "S1", null, 1);
            t.OnSessionAdopted("PM", "S2", "S1", 1);
            Assert.Contains("adopt L1 S2 changed", sink.Calls);
            Assert.Equal("L-cont", t.GetCurrentLaunchId("PM"));

            t.OnLaunchBegan("H", "Helper", "p", "c", "claude", null, launchGeneration: 1);
            t.LinkSpawner("H", "PM");
            Assert.Contains("link L2 <- L-cont", sink.Calls);
        }

        [Fact]
        public void No_adoption_is_written_once_MT_is_shutting_down()
        {
            var sink = new RecordingSink { ContinueAs = "L-cont" };
            var t = new PaneLaunchTracker(sink);
            t.OnLaunchBegan("PM", "Pat", "p", "c", "claude", null);
            t.BeginShutdown();
            t.OnSessionAdopted("PM", "S2", "S1");
            Assert.DoesNotContain(sink.Calls, c => c.StartsWith("adopt", StringComparison.Ordinal));
        }

        // ---- 7. launch kind and the helper-name rule ----

        [Fact]
        public void The_real_Claude_launch_command_classifies_as_Claude()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mt_kind_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string fresh = LaunchCommandBuilder.BuildClaudeCommand(null, dir).AutoRunCommand;
                string resumed = LaunchCommandBuilder.BuildClaudeCommand(null, dir, "0f8fad5b-d9cb-469f-a165-70867728950e").AutoRunCommand;
                Assert.Equal(PaneLaunchKind.Claude, PaneLaunchKind.FromAutoRunCommand(fresh));
                Assert.Equal(PaneLaunchKind.Claude, PaneLaunchKind.FromAutoRunCommand(resumed));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void A_terminal_kind_maps_to_its_launch_kind()
        {
            Assert.Equal(PaneLaunchKind.Claude, PaneLaunchKind.From(TerminalKind.ClaudeCode));
            Assert.Equal(PaneLaunchKind.Codex, PaneLaunchKind.From(TerminalKind.Codex));
        }

        [Theory]
        [InlineData("Notes", "Notes", true)]
        [InlineData("Notes", "notes", true)]
        [InlineData("Notes", "Notes-2", true)]
        [InlineData("Notes", "Agent-7", false)]
        [InlineData("Notes", null, false)]
        [InlineData("Notes", "", false)]
        [InlineData(null, "Notes", false)]
        public void A_pane_is_the_helper_only_under_its_name_or_a_suffixed_form(string requested, string resolved, bool expected)
        {
            Assert.Equal(expected, HelperResume.IsHelperName(requested, resolved));
        }
    }
}
