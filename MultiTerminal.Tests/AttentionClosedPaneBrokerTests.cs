using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Which broker teardown marks a terminal's pane CLOSED for the Attention rail (task 891488b3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service facts in <see cref="AttentionTerminalLifecycleTests"/> prove what a close mark
    /// does. This file pins who sets it, which is the half pipeline run 1 found wrong: the first
    /// version marked on every broker disconnect, and <c>/clear</c> is a broker disconnect
    /// (<see cref="MessageBroker.DisconnectTerminalByName"/>, called by the SessionEnd hook) in a pane
    /// that lives on. Its session-start question then got no card.
    /// </para>
    /// <para>
    /// <see cref="MessageBroker.UnregisterTerminal"/> is MultiTerminal itself tearing a terminal down
    /// (tab close, close_helper, Dispose, process exit, Launch-as), so it marks;
    /// <see cref="MessageBroker.DisconnectTerminalByName"/> does not.
    /// </para>
    /// </remarks>
    public sealed class AttentionClosedPaneBrokerTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _testMsgDbPath;

        public AttentionClosedPaneBrokerTests()
        {
            // Both databases a MessageBroker opens, each behind its own variable, so neither run
            // touches the production files (the LaunchNonceLookupTests idiom).
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_attnclose_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _testMsgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_attnclose_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _testMsgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[]
            {
                _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm",
                _testMsgDbPath, _testMsgDbPath + "-wal", _testMsgDbPath + "-shm",
            })
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
        }

        [Fact]
        public void Closing_the_pane_refuses_the_late_turn_end()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: null);
            broker.AgentAttention.NoteTerminalStarted("Alice");

            broker.UnregisterTerminal("DA");

            Assert.False(broker.AgentAttention.NoteTurnEnded("Alice", DateTime.UtcNow, isSubagent: false));
            Assert.Empty(broker.AgentAttention.Snapshot());
        }

        /// <summary>
        /// The /clear path. The notification is the session-start question, which is what the agent
        /// does next; before the fix moved the mark, it was refused.
        /// </summary>
        [Fact]
        public void A_session_end_disconnect_does_not_suppress_the_next_notification()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: null);
            broker.AgentAttention.NoteTerminalStarted("Alice");

            Assert.True(broker.DisconnectTerminalByName("Alice"));

            Assert.True(broker.AgentAttention.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = "sess-after-clear",
                ["agent_name"] = "Alice",
                ["raw_type"] = "ask_user_question",
                ["message"] = "What would you like to do?",
            }));
            Assert.Single(broker.AgentAttention.Snapshot());
        }

        /// <summary>
        /// The race from pipeline runs 1 and 2 (adversary HIGH), made deterministic. UnregisterTerminal
        /// raises TerminalDisconnected between reading the start token and closing, so a subscriber
        /// here runs at exactly the moment a same-name terminal could start. The close must then be
        /// void, and that terminal's one-shot question must get its card. This pins the broker's
        /// ORDER (token read before the close decision): read the token any later and it goes red.
        /// </summary>
        [Fact]
        public void A_same_name_start_during_the_close_voids_the_close()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: null);
            broker.AgentAttention.NoteTerminalStarted("Alice");
            broker.TerminalDisconnected += (_, t) => broker.AgentAttention.NoteTerminalStarted("Alice");

            broker.UnregisterTerminal("DA");

            Assert.True(broker.AgentAttention.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = "sess-relaunched",
                ["agent_name"] = "Alice",
                ["raw_type"] = "ask_user_question",
                ["message"] = "What would you like to do?",
            }));
        }

        /// <summary>
        /// The window pipeline run 3's adversary found: a same-name registration's row is LIVE, but the
        /// event that reaches NoteTerminalStarted has not run (here it never does: nothing in a bare
        /// broker test subscribes to TerminalRegistered). The close mark is still set, so only the
        /// refusal-time liveness check (the broker's LiveNameProbe wiring) can let the question through.
        /// </summary>
        [Fact]
        public void A_same_name_row_live_before_its_start_event_is_not_refused()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: null);
            broker.AgentAttention.NoteTerminalStarted("Alice");
            broker.UnregisterTerminal("DA");

            broker.RegisterTerminal("Alice", docId: "DB", nonce: null);
            Assert.True(broker.IsAgentNameHeldByLiveTerminal("Alice"), "precondition: the relaunch must be live");

            Assert.True(broker.AgentAttention.ApplyNotification(new Dictionary<string, object>
            {
                ["session_id"] = "sess-relaunched",
                ["agent_name"] = "Alice",
                ["raw_type"] = "ask_user_question",
                ["message"] = "What would you like to do?",
            }));
        }

        // The two-live-rows guard (another live terminal holds the name, so no mark) is pinned in
        // TerminalLivenessReaperTests, beside the profile fact it mirrors: that file owns the setup
        // that produces two connected rows with one name.
    }
}
