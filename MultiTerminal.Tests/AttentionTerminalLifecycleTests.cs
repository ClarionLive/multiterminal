using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// A card lives exactly as long as its terminal (task edcdcdd5, second pass).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner reported two lifecycle failures on the live rail: the card did not appear until
    /// 10-15 seconds after the terminal did (it was waiting for the first notification), and —
    /// found while fixing that — nothing ever removed a card, because
    /// <see cref="AgentAttentionService.Remove"/> and <see cref="AgentAttentionService.MarkOffline"/>
    /// had no callers at all.
    /// </para>
    /// <para>
    /// <see cref="AgentAttentionService.NoteTerminalStarted"/> and
    /// <see cref="AgentAttentionService.NoteTerminalGone"/> are the two edges, driven by the
    /// broker's <c>TerminalRegistered</c> / <c>TerminalDisconnected</c> in MainForm. These tests
    /// pin the service half; the wiring is two lines in MainForm and is not unit-testable here.
    /// </para>
    /// </remarks>
    public class AttentionTerminalLifecycleTests
    {
        private const string Agent = "Alice";

        private static Dictionary<string, object> Notification(string sessionId, string rawType = "permission_prompt")
            => new Dictionary<string, object>
            {
                ["session_id"] = sessionId,
                ["agent_name"] = Agent,
                ["notification_type"] = "permission_request",
                ["raw_type"] = rawType,
                ["message"] = "Alice needs permission to continue",
            };

        [Fact]
        public void A_started_terminal_gets_a_card_immediately_in_the_unknown_state()
        {
            var svc = new AgentAttentionService();
            AgentAttentionEntry raised = null;
            svc.AttentionChanged += (s, e) => raised = e;

            Assert.True(svc.NoteTerminalStarted(Agent));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal(Agent, card.AgentName);
            Assert.Equal(Agent, card.SessionId);
            Assert.Equal(AttentionState.Unknown, card.State);
            Assert.False(card.IsBlocking);
            Assert.NotNull(raised);
        }

        /// <summary>
        /// Registration fires more than once per terminal (pre-registration, then the agent's own
        /// <c>register_terminal</c>, then re-registrations). One terminal, one card.
        /// </summary>
        [Fact]
        public void A_second_start_for_the_same_agent_is_a_no_op()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            int events = 0;
            svc.AttentionChanged += (s, e) => events++;

            Assert.False(svc.NoteTerminalStarted(Agent));
            Assert.False(svc.NoteTerminalStarted("alice"));

            Assert.Single(svc.Snapshot());
            Assert.Equal(0, events);
        }

        /// <summary>
        /// Tool rows arrive keyed by agent name before any notification carries a session id. They
        /// must land on the placeholder rather than mint a duplicate.
        /// </summary>
        [Fact]
        public void Activity_before_the_first_notification_lands_on_the_placeholder()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);

            svc.NoteObservedActivity(Agent, DateTime.UtcNow, isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "Bash: git status");

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal(AttentionState.Working, card.State);
            Assert.Equal("Bash: git status", card.LastActivity);
        }

        /// <summary>
        /// The first session-keyed notification retires the name-keyed placeholder — and keeps the
        /// live line it had collected, with its own timestamp, so the card does not go blank at the
        /// exact moment it becomes interesting.
        /// </summary>
        [Fact]
        public void The_first_session_keyed_notification_supersedes_the_placeholder_and_keeps_its_line()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            var seen = DateTime.UtcNow.AddSeconds(-5);
            svc.NoteObservedActivity(Agent, seen, isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "Edit: MainForm.cs");

            AgentAttentionEntry removed = null;
            svc.AttentionRemoved += (s, e) => removed = e;

            Assert.True(svc.ApplyNotification(Notification("sess-1")));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal("sess-1", card.SessionId);
            Assert.Equal(AttentionState.BlockedPermission, card.State);
            Assert.Equal("Edit: MainForm.cs", card.LastActivity);
            Assert.Equal(seen, card.LastActivityAtUtc);

            Assert.NotNull(removed);
            Assert.Equal(Agent, removed.SessionId);
        }

        [Fact]
        public void A_newer_line_on_the_survivor_is_not_overwritten_by_the_placeholders_older_one()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            svc.NoteObservedActivity(Agent, DateTime.UtcNow.AddSeconds(-30), isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "old line");

            // A session-keyed observation arrives first (no notification yet), then the notification.
            svc.NoteObservedActivity("sess-1", DateTime.UtcNow, isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "new line");
            svc.ApplyNotification(Notification("sess-1"));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal("new line", card.LastActivity);
        }

        [Fact]
        public void A_gone_terminal_loses_its_card_and_nobody_elses()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            svc.NoteObservedActivity("sess-1", DateTime.UtcNow, isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "x");
            svc.NoteTerminalStarted("Bob");
            var removed = new List<AgentAttentionEntry>();
            svc.AttentionRemoved += (s, e) => removed.Add(e);

            Assert.True(svc.NoteTerminalGone(Agent));

            var left = svc.Snapshot();
            Assert.DoesNotContain(left, e => string.Equals(e.AgentName, Agent, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(left, e => e.AgentName == "Bob");
            Assert.Single(removed);
            Assert.All(removed, e => Assert.Equal(Agent, e.AgentName));
        }

        [Fact]
        public void Gone_for_an_unknown_agent_is_a_quiet_no_op()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted("Bob");
            int removed = 0;
            svc.AttentionRemoved += (s, e) => removed++;

            Assert.False(svc.NoteTerminalGone(Agent));
            Assert.False(svc.NoteTerminalGone(string.Empty));
            Assert.False(svc.NoteTerminalGone(null));

            Assert.Single(svc.Snapshot());
            Assert.Equal(0, removed);
        }

        [Fact]
        public void A_blank_name_never_creates_a_card()
        {
            var svc = new AgentAttentionService();

            Assert.False(svc.NoteTerminalStarted(null));
            Assert.False(svc.NoteTerminalStarted("   "));

            Assert.Empty(svc.Snapshot());
        }

        /// <summary>
        /// Open, work, close, reopen: the second life starts clean rather than inheriting a state
        /// from a terminal that no longer exists.
        /// </summary>
        [Fact]
        public void A_reopened_terminal_starts_from_unknown_again()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            svc.ApplyNotification(Notification("sess-1"));
            svc.NoteTerminalGone(Agent);

            Assert.True(svc.NoteTerminalStarted(Agent));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal(AttentionState.Unknown, card.State);
            Assert.Null(card.LastActivity);
        }
    }
}
