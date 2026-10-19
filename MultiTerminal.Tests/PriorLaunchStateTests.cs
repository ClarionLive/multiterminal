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
    /// "Previous-run launches are not 'open elsewhere'" (task 7d20e94c). The recent-write flag exists for
    /// processes outside MT; when the launch record shows a session's last process died with a previous
    /// MT run (still open = crash, or <c>mt_exit</c>), a quick relaunch must not flag the PM session or
    /// skip its helpers. A launch of THIS run that has not ended is open.
    /// </summary>
    public sealed class PriorLaunchStateTests : IDisposable
    {
        private const string Sid = "11111111-2222-3333-4444-555555555555";
        private const string Root = @"C:\proj";
        private static readonly DateTime T0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _testDbPath;

        public PriorLaunchStateTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_priorlaunch_{Guid.NewGuid():N}.db");
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

        private static PaneLaunchRecord Row(string run, int minute, string endReason = null, bool ended = false) => new PaneLaunchRecord
        {
            MtRunId = run,
            SessionId = Sid,
            StartedAt = T0.AddMinutes(minute),
            EndedAt = ended || endReason != null ? T0.AddMinutes(minute + 1) : (DateTime?)null,
            EndReason = endReason,
        };

        // ---- the pure classifier ----

        [Fact]
        public void No_row_is_no_record()
        {
            Assert.Equal(PriorLaunchState.NoRecord, PaneLaunchRecorder.ClassifyPriorLaunch(Array.Empty<PaneLaunchRecord>(), "now"));
        }

        [Theory]
        [InlineData(null)]                              // MT crashed with it open
        [InlineData(PaneLaunchEndReason.MtExit)]        // MT exited with it open
        public void A_previous_runs_open_or_mt_exit_launch_died_with_that_run(string reason)
        {
            Assert.Equal(PriorLaunchState.DiedWithPreviousRun, PaneLaunchRecorder.ClassifyPriorLaunch(new[] { Row("old", 0, reason) }, "now"));
        }

        [Theory]
        [InlineData(PaneLaunchEndReason.PaneClosed)]
        [InlineData(PaneLaunchEndReason.CloseHelper)]
        [InlineData(PaneLaunchEndReason.ProcessExit)]
        public void A_previous_runs_closed_launch_is_ended(string reason)
        {
            Assert.Equal(PriorLaunchState.Ended, PaneLaunchRecorder.ClassifyPriorLaunch(new[] { Row("old", 0, reason) }, "now"));
        }

        [Fact]
        public void This_runs_launch_is_open_until_it_ends()
        {
            Assert.Equal(PriorLaunchState.OpenInCurrentRun, PaneLaunchRecorder.ClassifyPriorLaunch(new[] { Row("now", 0) }, "now"));
            Assert.Equal(PriorLaunchState.Ended, PaneLaunchRecorder.ClassifyPriorLaunch(new[] { Row("now", 0, PaneLaunchEndReason.ProcessExit) }, "now"));
        }

        [Fact]
        public void Only_the_newest_launch_decides()
        {
            // An older crash row does not outvote a newer launch that ended this run, and vice versa.
            Assert.Equal(PriorLaunchState.Ended, PaneLaunchRecorder.ClassifyPriorLaunch(
                new[] { Row("old", 0), Row("now", 5, PaneLaunchEndReason.PaneClosed) }, "now"));
            Assert.Equal(PriorLaunchState.DiedWithPreviousRun, PaneLaunchRecorder.ClassifyPriorLaunch(
                new[] { Row("older", 9, PaneLaunchEndReason.MtExit), Row("oldest", 0, PaneLaunchEndReason.PaneClosed) }, "now"));
        }

        [Fact]
        public void The_recorder_classifies_against_its_own_run_id()
        {
            using var db = new TaskDatabase();
            var previous = new PaneLaunchRecorder(db, null, "run-old", () => T0);
            var id = previous.BeginLaunch("doc", "Notes", "p", Root, "claude", null);
            previous.AdoptSession(id, Sid);
            previous.EndAllForShutdown();

            var current = new PaneLaunchRecorder(db, null, "run-new", () => T0.AddMinutes(10));
            Assert.Equal(PriorLaunchState.DiedWithPreviousRun, current.GetPriorLaunchState(Sid));
            Assert.Equal(PriorLaunchState.Ended, previous.GetPriorLaunchState(Sid)); // its own run: mt_exit is an end
            Assert.Equal(PriorLaunchState.NoRecord, current.GetPriorLaunchState("99999999-2222-3333-4444-555555555555"));
        }

        // ---- the PM's "may be open elsewhere" flag in the launch prompt ----

        private static SessionCatalogEntry Recent(DateTime lastWrite) => new SessionCatalogEntry
        {
            SessionId = Sid,
            Cwd = Root,
            TranscriptPath = @"C:\t\" + Sid + ".jsonl",
            LastActivityUtc = lastWrite,
            Status = SessionCatalogStatus.PossiblyOpenElsewhere,
        };

        private static LaunchSessionCandidate Only(PriorLaunchState prior, params PaneSessionState[] panes) =>
            new LaunchSessionChoiceModel(new[] { Recent(T0) }, panes, _ => prior).Candidates.Single();

        [Fact]
        public void A_recent_session_that_died_with_the_previous_run_is_not_flagged()
        {
            Assert.False(Only(PriorLaunchState.DiedWithPreviousRun).MayBeOpenElsewhere);
        }

        [Theory]
        [InlineData(PriorLaunchState.NoRecord)]
        [InlineData(PriorLaunchState.Ended)]
        public void A_recent_session_the_record_cannot_account_for_is_still_flagged(PriorLaunchState prior)
        {
            Assert.True(Only(prior).MayBeOpenElsewhere);
        }

        [Fact]
        public void A_launch_of_this_run_that_has_not_ended_is_flagged_open()
        {
            var quiet = new SessionCatalogEntry { SessionId = Sid, Cwd = Root, LastActivityUtc = T0, Status = SessionCatalogStatus.Available };
            var c = new LaunchSessionChoiceModel(new[] { quiet }, null, _ => PriorLaunchState.OpenInCurrentRun).Candidates.Single();
            Assert.Equal(LaunchSessionChoiceModel.OpenInCurrentRunReason, c.MayBeOpenReason);
        }

        [Fact]
        public void The_waiver_does_not_touch_this_runs_unknown_pane_rule()
        {
            var unknownPane = new PaneSessionState(true, null, T0.AddSeconds(-1));
            Assert.True(Only(PriorLaunchState.DiedWithPreviousRun, unknownPane).MayBeOpenElsewhere);
        }

        [Fact]
        public void A_throwing_record_lookup_keeps_the_flag()
        {
            var c = new LaunchSessionChoiceModel(new[] { Recent(T0) }, null, _ => throw new InvalidOperationException()).Candidates.Single();
            Assert.True(c.MayBeOpenElsewhere);
        }

        // ---- the helper resume ----

        private static HelperResumeCheck CheckHelper(PriorLaunchState prior, params PaneSessionState[] panes) =>
            HelperResume.Check(
                new OpenHelper { SessionId = Sid, TerminalName = "Notes", Kind = "claude", Cwd = Root },
                Recent(T0), panes, Root, _ => T0, _ => true, prior);

        [Fact]
        public void A_helper_written_just_before_MT_went_away_is_resumed()
        {
            Assert.True(CheckHelper(PriorLaunchState.DiedWithPreviousRun).CanResume);
        }

        [Fact]
        public void A_recent_helper_with_no_record_is_still_skipped()
        {
            Assert.Contains("may still be open elsewhere", CheckHelper(PriorLaunchState.NoRecord).SkipReason);
        }

        [Fact]
        public void A_helper_still_running_in_this_run_is_skipped_as_open()
        {
            Assert.Contains("already open", CheckHelper(PriorLaunchState.OpenInCurrentRun).SkipReason);
        }

        [Fact]
        public void The_waiver_does_not_override_this_runs_live_set()
        {
            var holder = new PaneSessionState(true, Sid, T0.AddHours(-1));
            Assert.Contains("already open", CheckHelper(PriorLaunchState.DiedWithPreviousRun, holder).SkipReason);
        }
    }
}
