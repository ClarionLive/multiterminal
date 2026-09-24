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

        /// <summary>A pane close as the broker makes it: token read first, then the close.</summary>
        private static bool Close(AgentAttentionService svc) => svc.NoteTerminalClosed(Agent, svc.GetStartToken(Agent));

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

        /// <summary>
        /// The activity path creates name-keyed entries through Upsert, which sets only the key —
        /// AgentName stays null. Such a card must still be found by every by-agent operation, or
        /// it outlives its terminal. (Pipeline run 1, code review MAJOR.)
        /// </summary>
        [Fact]
        public void A_name_keyed_entry_with_no_agent_name_is_still_owned_by_the_agent()
        {
            var svc = new AgentAttentionService();
            svc.NoteActivityLineOnly(Agent, "Bash: git status", DateTime.UtcNow);
            Assert.Null(Assert.Single(svc.Snapshot()).AgentName);

            Assert.NotNull(svc.GetByAgent(Agent));
            Assert.False(svc.NoteTerminalStarted(Agent), "must not clobber the entry that is collecting activity");
            Assert.Equal("Bash: git status", Assert.Single(svc.Snapshot()).LastActivity);

            Assert.True(svc.NoteTerminalGone(Agent));
            Assert.Empty(svc.Snapshot());
        }

        [Fact]
        public void A_session_keyed_notification_retires_a_nameless_placeholder_too()
        {
            var svc = new AgentAttentionService();
            svc.NoteTurnEnded(Agent, DateTime.UtcNow.AddSeconds(-5), isSubagent: false);

            svc.ApplyNotification(Notification("sess-1"));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal("sess-1", card.SessionId);
            Assert.Equal("Turn ended", card.LastActivity);
        }

        /// <summary>
        /// A payload without agent_name must not blank a name already learned; otherwise the
        /// supersede finds nothing to retire and the terminal ends up with two cards, one of them
        /// pulsing forever. (Pipeline run 1, debugger MEDIUM.)
        /// </summary>
        [Fact]
        public void A_notification_without_an_agent_name_keeps_the_name_already_known()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("sess-1"));

            var nameless = Notification("sess-1", rawType: "elicitation_dialog");
            nameless.Remove("agent_name");
            svc.ApplyNotification(nameless);

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal(Agent, card.AgentName);
            Assert.Equal(AttentionState.BlockedQuestion, card.State);
        }

        [Fact]
        public void A_carried_line_is_announced_for_the_survivor()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            svc.NoteObservedActivity(Agent, DateTime.UtcNow.AddSeconds(-5), isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "Edit: A.cs");
            var announced = new List<AgentAttentionEntry>();
            svc.AttentionChanged += (s, e) => announced.Add(e);

            svc.ApplyNotification(Notification("sess-1"));

            Assert.Contains(announced, e => e.SessionId == "sess-1" && e.LastActivity == "Edit: A.cs");
        }

        /// <summary>
        /// The feed reader hands back DateTime.MinValue for a row whose timestamp it could not
        /// parse. That row must not become the timestamped live line — it would read as "quiet for
        /// 2000 years". (Pipeline run 2, debugger finding.)
        /// </summary>
        [Fact]
        public void A_line_with_no_usable_timestamp_is_not_recorded()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);

            Assert.False(svc.NoteActivityLineOnly(Agent, "Edit: Garbage.cs", DateTime.MinValue));
            Assert.False(svc.NoteActivityLineOnly(Agent, "Edit: Garbage.cs", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)));

            var card = Assert.Single(svc.Snapshot());
            Assert.Null(card.LastActivity);
            Assert.Null(card.LastActivityAtUtc);
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

        /// <summary>
        /// The sequence observed live (task 891488b3): a helper's Stop hook wrote its TURN_END row
        /// 172 ms AFTER close_helper had closed its pane and the card had been evicted. The watcher
        /// found no card for the name, fell back to the bare name as the key, and NoteTurnEnded
        /// minted a new entry with no AgentName, which the rail rendered as "(unnamed)" until MT
        /// restarted. Nothing else would ever remove it, because the close edge had already fired.
        /// </summary>
        [Fact]
        public void A_turn_end_that_lands_after_the_pane_closed_does_not_bring_the_card_back()
        {
            var svc = new AgentAttentionService();
            var closedAt = DateTime.UtcNow;
            svc.NoteTerminalStarted(Agent);
            svc.NoteObservedActivity(Agent, closedAt.AddSeconds(-4), isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "send_message");
            Close(svc);
            int announced = 0;
            svc.AttentionChanged += (s, e) => announced++;

            Assert.False(svc.NoteTurnEnded(Agent, closedAt.AddMilliseconds(172), isSubagent: false));

            Assert.Empty(svc.Snapshot());
            Assert.Null(svc.GetByAgent(Agent));
            Assert.Equal(0, announced);
        }

        /// <summary>
        /// The same race on every other way a closed pane's session can still reach the service: a
        /// tool row's display line, a tool row's clear edge (with and without an agent name), a
        /// notification keyed by the real session uuid (refused by its agent name, since its key is
        /// not a name), and MarkOffline.
        /// </summary>
        [Fact]
        public void Nothing_that_arrives_late_for_a_closed_pane_creates_a_card()
        {
            var svc = new AgentAttentionService();
            var late = DateTime.UtcNow.AddMilliseconds(200);
            svc.NoteTerminalStarted(Agent);
            svc.ApplyNotification(Notification("sess-1"));
            Close(svc);

            Assert.False(svc.NoteActivityLineOnly(Agent, "Bash: git status", late));
            Assert.False(svc.NoteObservedActivity(Agent, late, isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "Edit: A.cs"));

            // No agent name: the create branch then reads the name back from the entry it just made,
            // which a refused create no longer leaves behind. Must not throw.
            Assert.False(svc.NoteObservedActivity(Agent, late, isSubagent: false, toolUseId: null, agentName: null, activitySummary: "Edit: B.cs"));
            Assert.False(svc.ApplyNotification(Notification("sess-1")));
            Assert.False(svc.ApplyNotification(Notification("sess-2", rawType: "idle_prompt")));
            Assert.False(svc.MarkOffline(Agent));

            Assert.Empty(svc.Snapshot());
        }

        [Fact]
        public void The_closed_mark_is_case_insensitive_like_the_card_keys()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            Close(svc);

            Assert.False(svc.NoteTurnEnded("ALICE", DateTime.UtcNow, isSubagent: false));

            Assert.Empty(svc.Snapshot());
        }

        /// <summary>
        /// NoteTerminalClosed evicts as well as marking, so a card a late row created between the
        /// broker's own eviction (NoteTerminalGone, on the disconnect event) and the mark is removed.
        /// </summary>
        [Fact]
        public void A_card_created_between_the_disconnect_and_the_close_is_removed_by_the_close()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            svc.NoteTerminalGone(Agent);
            svc.NoteTurnEnded(Agent, DateTime.UtcNow, isSubagent: false);
            Assert.Single(svc.Snapshot());

            Assert.True(Close(svc));

            Assert.Empty(svc.Snapshot());
        }

        /// <summary>
        /// <c>/clear</c> disconnects the broker row through the SessionEnd hook while the pane and
        /// its agent carry on (pipeline run 1, debugger). The next thing that agent does is usually
        /// ask the session-start question, and that notification MUST get a card: a disconnect is not
        /// a close, and only a close may suppress.
        /// </summary>
        [Fact]
        public void A_disconnect_without_a_close_suppresses_nothing()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            svc.NoteTerminalGone(Agent);

            Assert.True(svc.ApplyNotification(Notification("sess-after-clear", rawType: "ask_user_question")));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal(AttentionState.BlockedQuestion, card.State);
        }

        /// <summary>
        /// The close path checks "no live terminal holds this name" and then closes, and a same-name
        /// registration can land between the two (pipeline runs 1 and 2, adversary). The close was
        /// decided about the OLD terminal, so it must neither mark nor evict: the new terminal's
        /// FIRST notification is one-shot, and a refused question would never be re-sent. The
        /// notification here is exactly that one-shot question, not replayable activity.
        /// </summary>
        [Fact]
        public void A_close_decided_before_a_same_name_start_touches_nothing()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            long tokenReadByTheClosePath = svc.GetStartToken(Agent);

            svc.NoteTerminalStarted(Agent);   // the same-name registration wins the race

            Assert.False(svc.NoteTerminalClosed(Agent, tokenReadByTheClosePath));
            Assert.Single(svc.Snapshot());   // the new terminal's card was not evicted
            Assert.True(svc.ApplyNotification(Notification("sess-new", rawType: "ask_user_question")));
            Assert.Equal(AttentionState.BlockedQuestion, Assert.Single(svc.Snapshot()).State);
        }

        /// <summary>
        /// A row is judged by when its hook WROTE it, not by when the watcher gets to it (pipeline
        /// run 2, code review + adversary). The watcher's poll interval is configurable up to 60 s, so
        /// judging by processing time let the very straggler this ticket is about through again
        /// whenever a poll ran late.
        /// </summary>
        [Fact]
        public void A_straggler_processed_long_after_the_close_is_still_refused()
        {
            var now = DateTime.UtcNow;
            var svc = new AgentAttentionService { UtcNow = () => now };
            svc.NoteTerminalStarted(Agent);
            var closedAt = now;
            Close(svc);

            now = closedAt + TimeSpan.FromSeconds(45);   // a slow poll, well past the grace

            Assert.False(svc.NoteTurnEnded(Agent, closedAt.AddMilliseconds(172), isSubagent: false));
            Assert.Empty(svc.Snapshot());
        }

        /// <summary>
        /// The other side of the evidence rule: a row stamped after the grace is not the closed
        /// pane's output. Pinned at the boundary, one millisecond each way.
        /// </summary>
        [Fact]
        public void Evidence_stamped_after_the_grace_creates_a_card()
        {
            var now = DateTime.UtcNow;
            var svc = new AgentAttentionService { UtcNow = () => now };
            svc.NoteTerminalStarted(Agent);
            var closedAt = now;
            Close(svc);

            Assert.False(svc.NoteTurnEnded(Agent, closedAt + AgentAttentionService.ClosedPaneGrace - TimeSpan.FromMilliseconds(1), isSubagent: false));
            Assert.True(svc.NoteTurnEnded(Agent, closedAt + AgentAttentionService.ClosedPaneGrace + TimeSpan.FromMilliseconds(1), isSubagent: false));
            Assert.Single(svc.Snapshot());
        }

        /// <summary>
        /// A notification carries no timestamp of its own, so it is judged at the moment it arrives.
        /// </summary>
        [Fact]
        public void A_notification_is_judged_by_when_it_arrives()
        {
            var now = DateTime.UtcNow;
            var svc = new AgentAttentionService { UtcNow = () => now };
            svc.NoteTerminalStarted(Agent);
            var closedAt = now;
            Close(svc);

            now = closedAt + TimeSpan.FromSeconds(1);
            Assert.False(svc.ApplyNotification(Notification("sess-1")));

            now = closedAt + AgentAttentionService.ClosedPaneGrace + TimeSpan.FromSeconds(1);
            Assert.True(svc.ApplyNotification(Notification("sess-1")));
        }

        /// <summary>
        /// The mark must not outlive the NEXT terminal with that name: a relaunched Alice gets her
        /// card back at once, not after the grace period, and her activity lands on it.
        /// </summary>
        [Fact]
        public void A_relaunched_terminal_with_the_same_name_is_tracked_again()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            Close(svc);

            Assert.True(svc.NoteTerminalStarted(Agent));
            Assert.True(svc.NoteObservedActivity(Agent, DateTime.UtcNow, isSubagent: false, toolUseId: null, agentName: Agent, activitySummary: "Bash: git status"));
            Assert.True(svc.ApplyNotification(Notification("sess-2")));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal("sess-2", card.SessionId);
            Assert.Equal(Agent, card.AgentName);
            Assert.Equal(AttentionState.BlockedPermission, card.State);
        }

        [Fact]
        public void One_agents_closed_mark_does_not_touch_another_agent()
        {
            var svc = new AgentAttentionService();
            svc.NoteTerminalStarted(Agent);
            Close(svc);

            Assert.True(svc.NoteTurnEnded("Bob", DateTime.UtcNow, isSubagent: false));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal("Bob", card.SessionId);
        }

        /// <summary>
        /// Only a name whose pane CLOSED is refused. An agent the service never saw start (the
        /// existing facts above build cards that way) still gets a card from its first observation.
        /// </summary>
        [Fact]
        public void An_agent_never_seen_starting_is_still_tracked_as_before()
        {
            var svc = new AgentAttentionService();

            Assert.True(svc.NoteTurnEnded(Agent, DateTime.UtcNow, isSubagent: false));

            Assert.Single(svc.Snapshot());
        }
    }
}
