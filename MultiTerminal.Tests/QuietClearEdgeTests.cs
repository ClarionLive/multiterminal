using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The clear-only lane: <c>TOOL_QUIET</c> (task edcdcdd5, Owner's live pass 2026-09-03).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect these cover is not a coding slip — it is two consumers sharing one filter.
    /// <c>activity-hook.js</c>'s <c>SKIP_TOOLS</c> dropped read-only tool completions to keep the
    /// human-facing Activity feed readable, which is a good reason that concerns ONE consumer. The
    /// Attention Rail's clear-edge read the same rows for the opposite purpose: a completed Read
    /// says nothing worth displaying but proves the agent is running again.
    /// </para>
    /// <para>
    /// Fused, a card stayed blocked through any read-only stretch and — the case the Owner hit —
    /// after every answered question. <c>AskUserQuestion</c> emits NO hook event at all (verified
    /// against a 71MB hook log: zero <c>tool=AskUserQuestion</c> entries across 150k+ invocations)
    /// and blocks rather than ending a turn, so it produces neither a completion row nor a
    /// <c>TURN_END</c>. Nothing in the system observes the owner answering; the fix therefore had
    /// to make the NEXT thing the agent does count, whatever that is.
    /// </para>
    /// </remarks>
    public sealed class QuietClearEdgeTests : IDisposable
    {
        private const string Agent = "Alice";
        private const string Session = "sess-quiet";

        private readonly string _dbPath;
        private readonly TaskDatabase _taskDb;
        private readonly ActivityFeedService _feed;
        private readonly AgentAttentionService _attention;
        private readonly AgentActivityWatcher _watcher;
        private readonly List<string> _log = new List<string>();

        public QuietClearEdgeTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_quiet_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            _taskDb = new TaskDatabase();
            _feed = new ActivityFeedService();
            _attention = new AgentAttentionService();
            _watcher = new AgentActivityWatcher(_feed, _attention, _log.Add);
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _feed.Dispose();
            _taskDb.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
        }

        // ─────────────────────────────────────────────────────────────────

        private static string MainThread(string tool) => $"{{\"tool\":\"{tool}\",\"agent_id\":null,\"session_id\":\"{Session}\"}}";

        private static string Subagent(string tool) => $"{{\"tool\":\"{tool}\",\"agent_id\":\"sub-7\",\"session_id\":\"{Session}\"}}";

        private void Block()
        {
            Assert.True(_attention.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = Session,
                ["agent_name"] = Agent,
                ["notification_type"] = "permission_request",
                ["raw_type"] = "permission_prompt",
                ["message"] = "Alice is waiting for your input",
            }));

            // Same reason as AgentActivityWatcherPollTests.Block: the clear path refuses activity
            // that does not post-date the block, and the Windows clock ticks at ~1-15ms.
            Thread.Sleep(30);
        }

        private void Row(string type, string summary, string details) =>
            _feed.RecordGeneralActivity(type, Agent, summary, "info", details);

        private AgentAttentionEntry Entry() => _attention.Get(Session);

        // ───────────────────────── the clear-only lane ─────────────────────

        /// <summary>
        /// The whole point: the card stops shouting even though the tool that ran was one whose
        /// name is never worth displaying.
        /// </summary>
        [Fact]
        public void Tool_quiet_clears_a_block()
        {
            Assert.True(_watcher.Prime());
            Block();
            Assert.Equal(AttentionState.BlockedPermission, Entry().State);

            Row("TOOL_QUIET", "Read", MainThread("Read"));
            _watcher.Poll();

            Assert.Equal(AttentionState.Working, Entry().State);
        }

        /// <summary>
        /// The exact mirror of <c>TOOL_START</c>, and the half that makes the split worth having.
        /// If a quiet row could write the line, the Activity feed's noise would simply reappear in
        /// a more prominent place — one line per card, at the top of the rail.
        /// </summary>
        [Fact]
        public void Tool_quiet_does_not_touch_the_display_line()
        {
            Assert.True(_watcher.Prime());

            // Block first so the entry exists under the SESSION key. Without a notification the
            // watcher's ResolveSessionKey falls back to the agent NAME, and the entry lands under
            // a different key than Entry() reads — a property of key resolution, not of this lane.
            Block();

            Row("TOOL_COMPLETE", "Edit: MainForm.cs", MainThread("Edit"));
            _watcher.Poll();
            Assert.Equal("Edit: MainForm.cs", Entry().LastActivity);
            DateTime? stampBefore = Entry().LastActivityAtUtc;

            Thread.Sleep(30);
            Row("TOOL_QUIET", "Read", MainThread("Read"));
            _watcher.Poll();

            var e = Entry();
            Assert.Equal("Edit: MainForm.cs", e.LastActivity);
            Assert.Equal(stampBefore, e.LastActivityAtUtc);
        }

        /// <summary>
        /// A quiet row is NOT exempt from the ordering guard. Activity that predates the block is
        /// the queue draining, not the owner answering — the same rule every other clearing row
        /// obeys. Getting this wrong would silence a card at the instant of its creation, which is
        /// finding 1 of the 2289bb8a spike arriving by a new door.
        /// </summary>
        [Fact]
        public void Tool_quiet_that_predates_the_block_does_not_clear()
        {
            Assert.True(_watcher.Prime());

            Row("TOOL_QUIET", "Read", MainThread("Read"));
            Thread.Sleep(30);
            Block();

            _watcher.Poll();

            Assert.Equal(AttentionState.BlockedPermission, Entry().State);
        }

        /// <summary>
        /// A subagent's read proves nothing about its parent, exactly as its Edit proves nothing.
        /// The refusal must not have been bypassed by adding a lane.
        /// </summary>
        [Fact]
        public void A_subagents_quiet_row_does_not_clear_its_parent()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("TOOL_QUIET", "Read", Subagent("Read"));
            _watcher.Poll();

            Assert.Equal(AttentionState.BlockedPermission, Entry().State);
        }

        /// <summary>
        /// Regression guard on the rule the new lane sits next to. TOOL_START is the single
        /// easiest thing here to "simplify" into the clear set, and adding a second clearing type
        /// makes that mistake easier, not harder.
        /// </summary>
        [Fact]
        public void Tool_start_still_never_clears()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("TOOL_START", "Bash: git status", MainThread("Bash"));
            _watcher.Poll();

            Assert.Equal(AttentionState.BlockedPermission, Entry().State);
        }

        // ──────────────────── hidden from the human feeds ──────────────────

        /// <summary>
        /// The readability that <c>SKIP_TOOLS</c> was protecting is now this filter's job. If it
        /// regresses, the Activity panel fills with Read lines and the original reason for
        /// dropping the rows is defeated — while every clear-edge test above still passes.
        /// </summary>
        [Fact]
        public void Quiet_rows_are_hidden_from_the_human_facing_readers()
        {
            Row("TOOL_COMPLETE", "Edit: MainForm.cs", MainThread("Edit"));
            Row("TOOL_QUIET", "Read", MainThread("Read"));

            var since = _feed.GetActivitiesSince(DateTime.UtcNow.AddHours(-1), 100);
            Assert.DoesNotContain(since, r => r.ActivityType == "TOOL_QUIET");
            Assert.Contains(since, r => r.ActivityType == "TOOL_COMPLETE");

            var recent = _feed.GetRecentActivities(100);
            Assert.DoesNotContain(recent, r => r.ActivityType == "TOOL_QUIET");
            Assert.Contains(recent, r => r.ActivityType == "TOOL_COMPLETE");
        }

        /// <summary>
        /// The other direction, and the one a "tidy up the filters" change would break silently:
        /// the WATCHER's reader must still see quiet rows. Filtering them everywhere would restore
        /// the original bug while leaving the panel tests green.
        /// </summary>
        [Fact]
        public void The_watchers_reader_still_sees_quiet_rows()
        {
            Row("TOOL_QUIET", "Read", MainThread("Read"));

            var forWatcher = _feed.GetActivitiesAfterId(0, 200);

            Assert.Contains(forWatcher, r => r.ActivityType == "TOOL_QUIET");
        }

        /// <summary>
        /// End to end, in the order the owner experiences it: a question blocks the card, the
        /// owner answers, and the agent's next move is a Read — the case that used to leave the
        /// card pulsing until some unrelated Bash came along.
        /// </summary>
        [Fact]
        public void Answering_a_question_then_reading_clears_the_card()
        {
            Assert.True(_watcher.Prime());
            Block();
            Assert.True(Entry().IsBlocking);

            // No row exists for the answer itself — nothing in the system observes it.
            Row("TOOL_QUIET", "Read", MainThread("Read"));
            _watcher.Poll();

            Assert.False(Entry().IsBlocking);
        }
    }
}
