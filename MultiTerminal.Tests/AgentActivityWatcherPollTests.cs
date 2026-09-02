using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Drives <see cref="AgentActivityWatcher.Poll"/> against a REAL temp SQLite database
    /// (task edcdcdd5, second pass).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first pass shipped with the routing table — which <c>activity_feed</c> row types clear a
    /// block, which only feed the display line, which are ignored — verified by READING. The rules
    /// were tested at the service level; the mapping from a row in the table to a method on the
    /// service was not. The ticket's own continuation note named this as the real gap.
    /// </para>
    /// <para>
    /// It then turned out the watcher was never even constructed at runtime, and the hook that
    /// writes the rows was writing them to a database with no tables. Neither of those is
    /// something a unit test can see — they are integration failures, caught by the owner. What
    /// this file CAN do is make sure that, given rows in the right table and a watcher that has
    /// been started, the routing is what the design says: so the next integration failure is at
    /// least not hiding a routing bug behind it.
    /// </para>
    /// </remarks>
    public sealed class AgentActivityWatcherPollTests : IDisposable
    {
        private const string Agent = "Alice";
        private const string Session = "sess-live";

        private readonly string _dbPath;
        private readonly TaskDatabase _taskDb;
        private readonly ActivityFeedService _feed;
        private readonly AgentAttentionService _attention;
        private readonly AgentActivityWatcher _watcher;
        private readonly List<string> _log = new List<string>();

        public AgentActivityWatcherPollTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_watcher_{Guid.NewGuid():N}.db");
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
                ["message"] = "Alice needs permission to continue",
            }));

            // The clear path refuses activity that does not post-date the block. Both stamps are
            // DateTime.UtcNow and the Windows clock ticks at ~1-15ms, so without this gap the row
            // could land on the same tick as the block and be refused for a reason that has
            // nothing to do with routing.
            Thread.Sleep(30);
        }

        private void Row(string type, string summary, string details) =>
            _feed.RecordGeneralActivity(type, Agent, summary, "info", details);

        private AgentAttentionEntry Entry() => _attention.Get(Session);

        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Tool_complete_from_the_main_thread_clears_a_block_and_sets_the_line()
        {
            Assert.True(_watcher.Prime());
            Block();
            Assert.Equal(AttentionState.BlockedPermission, Entry().State);

            Row("TOOL_COMPLETE", "Edit: MainForm.cs", MainThread("Edit"));
            _watcher.Poll();

            var e = Entry();
            Assert.Equal(AttentionState.Working, e.State);
            Assert.Equal("Edit: MainForm.cs", e.LastActivity);
            Assert.NotNull(e.LastActivityAtUtc);
        }

        [Fact]
        public void Tool_failed_clears_too_because_a_denied_permission_is_a_failure()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("TOOL_FAILED", "Bash failed: denied", MainThread("Bash"));
            _watcher.Poll();

            Assert.Equal(AttentionState.Working, Entry().State);
        }

        /// <summary>
        /// TOOL_START is written on PreToolUse, and the safety hook is itself a PreToolUse hook
        /// that returns <c>ask</c> — so the row precedes the prompt it causes. Clearing on it
        /// would clear a block at the instant of its creation. It may move the line; it may not
        /// move the state. This is the one row type most tempting to "simplify" into the clear set.
        /// </summary>
        [Fact]
        public void Tool_start_moves_the_line_but_never_clears()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("TOOL_START", "Bash: git status", MainThread("Bash"));
            _watcher.Poll();

            var e = Entry();
            Assert.Equal(AttentionState.BlockedPermission, e.State);
            Assert.Equal("Bash: git status", e.LastActivity);
        }

        [Fact]
        public void Turn_end_is_the_escape_edge_and_lands_on_idle()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("TURN_END", "Turn ended", MainThread(string.Empty));
            _watcher.Poll();

            var e = Entry();
            Assert.Equal(AttentionState.Idle, e.State);
            Assert.False(e.IsBlocking);
        }

        /// <summary>
        /// A subagent's rows are logged under the PARENT's name. Clearing on them would render a
        /// calm card for a parent still waiting — the silent failure this whole feature is built
        /// around avoiding. The row is dropped outright: not even the display line moves, because a
        /// line describing the subagent's work under the parent's name is a mislabel.
        /// </summary>
        [Fact]
        public void Subagent_rows_change_nothing()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("TOOL_COMPLETE", "Edit: Worker.cs", Subagent("Edit"));
            Row("TURN_END", "Turn ended", Subagent(string.Empty));
            _watcher.Poll();

            var e = Entry();
            Assert.Equal(AttentionState.BlockedPermission, e.State);
            Assert.Null(e.LastActivity);
        }

        /// <summary>
        /// A row with NO provenance is one written before the hook stamped it — it cannot say
        /// whether it was a subagent, so it is treated as one. Read the other way, the safe default
        /// inverts on exactly the historical data.
        /// </summary>
        [Fact]
        public void Rows_without_provenance_are_treated_as_possibly_subagent_and_ignored()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("TOOL_COMPLETE", "Edit: Old.cs", null);
            Row("TOOL_COMPLETE", "Edit: Older.cs", "{\"tool\":\"Edit\"}");
            _watcher.Poll();

            Assert.Equal(AttentionState.BlockedPermission, Entry().State);
        }

        /// <summary>
        /// Everything already in the table when the watcher starts is history. Replaying it would
        /// apply long-dead tool events to live state — clearing a block raised after them.
        /// </summary>
        [Fact]
        public void Rows_written_before_prime_are_never_applied()
        {
            Block();
            Row("TOOL_COMPLETE", "Edit: Stale.cs", MainThread("Edit"));

            Assert.True(_watcher.Prime());
            _watcher.Poll();

            var e = Entry();
            Assert.Equal(AttentionState.BlockedPermission, e.State);
            Assert.Null(e.LastActivity);
        }

        /// <summary>
        /// The watermark must advance past a row that cannot be applied, or that row is retried
        /// on every tick forever and everything after it waits behind it.
        /// </summary>
        [Fact]
        public void An_unusable_row_does_not_stall_the_rows_behind_it()
        {
            Assert.True(_watcher.Prime());
            Block();

            _feed.RecordGeneralActivity("TOOL_COMPLETE", actor: null, summary: "no actor", severity: "info", detailsJson: MainThread("Edit"));
            Row("TOOL_COMPLETE", "Edit: After.cs", MainThread("Edit"));
            _watcher.Poll();

            Assert.Equal(AttentionState.Working, Entry().State);
            Assert.Equal("Edit: After.cs", Entry().LastActivity);
        }

        /// <summary>
        /// Rows for an agent that has no card yet still produce one, keyed by name — the same key
        /// <see cref="AgentAttentionService.ApplyNotification"/> falls back to, so a later
        /// notification without a session id lands on the same entry rather than a duplicate.
        /// </summary>
        [Fact]
        public void An_agent_with_no_card_gets_one_keyed_by_name()
        {
            Assert.True(_watcher.Prime());

            Row("TOOL_COMPLETE", "Write: New.cs", MainThread("Write"));
            _watcher.Poll();

            var e = _attention.GetByAgent(Agent);
            Assert.NotNull(e);
            Assert.Equal(Agent, e.SessionId);
            Assert.Equal(AttentionState.Working, e.State);
            Assert.Equal("Write: New.cs", e.LastActivity);
        }

        [Fact]
        public void Unrelated_row_types_are_ignored_entirely()
        {
            Assert.True(_watcher.Prime());
            Block();

            Row("task_checklist_transition", "Checklist item moved", MainThread(string.Empty));
            Row("worktree_janitor_sweep", "Janitor swept", MainThread(string.Empty));
            _watcher.Poll();

            var e = Entry();
            Assert.Equal(AttentionState.BlockedPermission, e.State);
            Assert.Null(e.LastActivity);
        }
    }
}
