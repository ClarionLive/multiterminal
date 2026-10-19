using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <c>pane_launches</c> and <see cref="PaneLaunchRecorder"/> (task 7d20e94c, item 0), through the REAL
    /// <see cref="TaskDatabase"/> on a temp SQLite file. The rule these pin is which helper sessions a PM
    /// session left open: direct children of any launch of the PM session, newest launch per helper
    /// session, open only when never ended or ended by an MT exit.
    /// </summary>
    public sealed class PaneLaunchRecorderTests : IDisposable
    {
        private const string Pm = "pm-session";
        private static readonly DateTime T0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _testDbPath;
        private DateTime _now = T0;

        public PaneLaunchRecorderTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_panelaunch_{Guid.NewGuid():N}.db");
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

        private PaneLaunchRecorder Recorder(TaskDatabase db, string runId = "run-1", List<string> errors = null)
            => new PaneLaunchRecorder(db, m => errors?.Add(m), runId, () => _now);

        /// <summary>Starts a launch one minute after the previous one, so launch order is unambiguous.</summary>
        private string Launch(PaneLaunchRecorder rec, string name, string sessionId, string spawnerLaunchId = null)
        {
            _now = _now.AddMinutes(1);
            var id = rec.BeginLaunch("doc-" + name, name, "proj", @"C:\work\" + name, "claude", null);
            Assert.NotNull(id);
            if (sessionId != null) rec.AdoptSession(id, sessionId);
            if (spawnerLaunchId != null) rec.LinkSpawner(id, spawnerLaunchId);
            return id;
        }

        private static string[] Sessions(IReadOnlyList<OpenHelper> helpers) => helpers.Select(h => h.SessionId).ToArray();

        [Fact]
        public void Migration_is_idempotent_and_creates_the_table_and_indexes()
        {
            using (var first = new TaskDatabase())
            {
                Recorder(first).BeginLaunch("d", "n", "p", "c", "claude", null);
            }
            SQLiteConnection.ClearAllPools();

            // Forget the ledger entry, so the reopen really runs the migration again over the existing table
            // (with the entry present the runner skips it, and this test would prove nothing about the migration).
            using (var ledger = new SQLiteConnection($"Data Source={_testDbPath}"))
            {
                ledger.Open();
                using var forget = new SQLiteCommand("DELETE FROM schema_migrations WHERE name = 'MigrateAddPaneLaunches'", ledger);
                Assert.Equal(1, forget.ExecuteNonQuery());
            }
            SQLiteConnection.ClearAllPools();

            using var db = new TaskDatabase();

            using var conn = new SQLiteConnection($"Data Source={_testDbPath}");
            conn.Open();
            using var cmd = new SQLiteCommand(
                "SELECT name FROM sqlite_master WHERE tbl_name = 'pane_launches' ORDER BY name", conn);
            var names = new List<string>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetString(0));
            Assert.Contains("pane_launches", names);
            Assert.Contains("idx_pane_launches_session", names);
            Assert.Contains("idx_pane_launches_spawner", names);
            Assert.Contains("idx_pane_launches_run", names);

            // The row written before the reopen survived it: re-running the migration did not recreate the table.
            using var count = new SQLiteCommand("SELECT COUNT(*) FROM pane_launches", conn);
            Assert.Equal(1L, (long)count.ExecuteScalar());
        }

        [Fact]
        public void An_open_helper_is_returned_with_its_launch_details()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Pat", Pm);
            Launch(rec, "Helper1", "h1", pm);

            var helper = Assert.Single(rec.GetOpenHelpers(Pm));
            Assert.Equal("h1", helper.SessionId);
            Assert.Equal("Helper1", helper.TerminalName);
            Assert.Equal(@"C:\work\Helper1", helper.Cwd);
            Assert.Equal("proj", helper.ProjectId);
            Assert.Equal("claude", helper.Kind);
            Assert.Equal(_now, helper.LastStartedAt);
        }

        [Theory]
        [InlineData(PaneLaunchEndReason.PaneClosed)]
        [InlineData(PaneLaunchEndReason.CloseHelper)]
        [InlineData(PaneLaunchEndReason.ProcessExit)]
        public void A_helper_that_ended_closed_is_excluded(string reason)
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Pat", Pm);
            var h = Launch(rec, "Helper1", "h1", pm);
            Launch(rec, "Helper2", "h2", pm);
            rec.End(h, reason);

            Assert.Equal(new[] { "h2" }, Sessions(rec.GetOpenHelpers(Pm)));
        }

        [Fact]
        public void Mt_exit_and_a_crash_both_count_as_open()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Pat", Pm);
            Launch(rec, "Exited", "h-exit", pm);
            rec.EndAllForShutdown();

            // A later run whose MT crashed: nothing ever ended its rows.
            var crashed = Recorder(db, "run-2");
            var pm2 = Launch(crashed, "Pat", Pm);
            Launch(crashed, "Crashed", "h-crash", pm2);

            Assert.Equal(PaneLaunchEndReason.MtExit, db.GetPaneLaunchesBySession("h-exit").Single().EndReason);
            Assert.Equal(new[] { "h-crash", "h-exit" }, Sessions(crashed.GetOpenHelpers(Pm)));
        }

        [Fact]
        public void Helpers_spawned_under_either_launch_of_a_resumed_pm_session_are_merged()
        {
            using var db = new TaskDatabase();
            var run1 = Recorder(db, "run-1");
            var pmFirst = Launch(run1, "Pat", Pm);
            Launch(run1, "Early", "h-early", pmFirst);
            run1.EndAllForShutdown();

            var run2 = Recorder(db, "run-2");
            var pmResumed = Launch(run2, "Pat", Pm);
            Launch(run2, "Late", "h-late", pmResumed);

            Assert.Equal(new[] { "h-late", "h-early" }, Sessions(run2.GetOpenHelpers(Pm)));
        }

        [Fact]
        public void One_row_per_helper_session_from_its_newest_launch()
        {
            using var db = new TaskDatabase();
            var run1 = Recorder(db, "run-1");
            var pmFirst = Launch(run1, "Pat", Pm);
            Launch(run1, "OldName", "h1", pmFirst);
            run1.EndAllForShutdown();

            var run2 = Recorder(db, "run-2");
            var pmResumed = Launch(run2, "Pat", Pm);
            Launch(run2, "NewName", "h1", pmResumed);

            var helper = Assert.Single(run2.GetOpenHelpers(Pm));
            Assert.Equal("NewName", helper.TerminalName);
        }

        [Fact]
        public void A_newer_closed_launch_of_the_helper_session_drops_it_even_if_an_older_one_is_open()
        {
            using var db = new TaskDatabase();
            var run1 = Recorder(db, "run-1");
            var pmFirst = Launch(run1, "Pat", Pm);
            Launch(run1, "Helper", "h1", pmFirst);
            run1.EndAllForShutdown();

            var run2 = Recorder(db, "run-2");
            var pmResumed = Launch(run2, "Pat", Pm);
            var resumedHelper = Launch(run2, "Helper", "h1", pmResumed);
            run2.End(resumedHelper, PaneLaunchEndReason.CloseHelper);

            Assert.Empty(run2.GetOpenHelpers(Pm));
        }

        [Fact]
        public void A_later_closed_launch_spawned_by_nobody_also_drops_the_helper()
        {
            using var db = new TaskDatabase();
            var run1 = Recorder(db, "run-1");
            var pm = Launch(run1, "Pat", Pm);
            Launch(run1, "Helper", "h1", pm);
            run1.EndAllForShutdown();

            // The helper session was later resumed on its own (no spawner link) and closed there.
            var run2 = Recorder(db, "run-2");
            var alone = Launch(run2, "Helper", "h1");
            run2.End(alone, PaneLaunchEndReason.PaneClosed);

            Assert.Empty(run2.GetOpenHelpers(Pm));
        }

        [Fact]
        public void Helpers_of_helpers_and_the_pm_own_session_are_excluded()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Pat", Pm);
            var child = Launch(rec, "Child", "h-child", pm);
            Launch(rec, "Grandchild", "h-grand", child);
            Launch(rec, "Self", Pm, pm);
            Launch(rec, "NoSession", null, pm);

            Assert.Equal(new[] { "h-child" }, Sessions(rec.GetOpenHelpers(Pm)));
        }

        [Fact]
        public void The_cap_keeps_the_newest()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var pm = Launch(rec, "Pat", Pm);
            for (int i = 1; i <= 8; i++) Launch(rec, "H" + i, "h" + i, pm);

            Assert.Equal(new[] { "h8", "h7", "h6", "h5", "h4", "h3" }, Sessions(rec.GetOpenHelpers(Pm)));
            Assert.Equal(new[] { "h8", "h7" }, Sessions(rec.GetOpenHelpers(Pm, 2)));
        }

        [Fact]
        public void Ending_a_run_touches_only_that_runs_open_rows()
        {
            using var db = new TaskDatabase();
            var run1 = Recorder(db, "run-1");
            var run2 = Recorder(db, "run-2");
            var open1 = Launch(run1, "A", "a");
            var closed1 = Launch(run1, "B", "b");
            run1.End(closed1, PaneLaunchEndReason.PaneClosed);
            var open2 = Launch(run2, "C", "c");

            Assert.Equal(1, db.EndOpenPaneLaunchesForRun("run-1", PaneLaunchEndReason.MtExit));

            Assert.Equal(PaneLaunchEndReason.MtExit, db.GetPaneLaunch(open1).EndReason);
            Assert.Equal(PaneLaunchEndReason.PaneClosed, db.GetPaneLaunch(closed1).EndReason);
            var other = db.GetPaneLaunch(open2);
            Assert.Null(other.EndedAt);
            Assert.Null(other.EndReason);
        }

        [Fact]
        public void End_does_not_overwrite_an_earlier_end()
        {
            using var db = new TaskDatabase();
            var rec = Recorder(db);
            var id = Launch(rec, "Helper", "h1");

            Assert.True(db.EndPaneLaunch(id, PaneLaunchEndReason.CloseHelper));
            var firstEnd = db.GetPaneLaunch(id).EndedAt;
            Assert.False(db.EndPaneLaunch(id, PaneLaunchEndReason.ProcessExit));
            rec.EndAllForShutdown();

            var row = db.GetPaneLaunch(id);
            Assert.Equal(PaneLaunchEndReason.CloseHelper, row.EndReason);
            Assert.Equal(firstEnd, row.EndedAt);
        }

        [Fact]
        public void The_recorder_never_throws_and_logs_instead()
        {
            var errors = new List<string>();
            TaskDatabase db;
            using (db = new TaskDatabase()) { }
            var rec = Recorder(db, errors: errors);

            Assert.Null(rec.BeginLaunch("d", "n", "p", "c", "claude", null));
            rec.AdoptSession("x", "s");
            rec.LinkSpawner("x", "y");
            rec.End("x", PaneLaunchEndReason.PaneClosed);
            rec.EndAllForShutdown();
            Assert.Empty(rec.GetOpenHelpers(Pm));

            Assert.Equal(6, errors.Count);
        }
    }
}
