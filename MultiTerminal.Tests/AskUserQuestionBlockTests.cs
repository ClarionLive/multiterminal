using System;
using System.Collections.Generic;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// A pending question is a block, and an idle timer must not talk it away (task ee17f42d).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Owner found this in the most direct way available: they were reading a question this
    /// agent had just asked, and the card said <c>"Finished and idle"</c>.
    /// </para>
    /// <para>
    /// That is the failure this whole ticket family exists to prevent, in its worst form. A card
    /// that goes quiet merely fails to help. A card that reads "Finished and idle" in front of an
    /// agent blocked on the Owner asserts the OPPOSITE of the truth, confidently, in the one place
    /// built to answer "who needs me?".
    /// </para>
    /// </remarks>
    public sealed class AskUserQuestionBlockTests
    {
        private const string Session = "sess-q";
        private const string Agent = "Alice";

        private static Dictionary<string, object> Payload(string rawType, string message = "msg") =>
            new Dictionary<string, object>
            {
                ["session_id"] = Session,
                ["agent_name"] = Agent,
                ["notification_type"] = "permission_request",
                ["raw_type"] = rawType,
                ["message"] = message,
            };

        // ───────────────────── the question is a block ─────────────────────

        /// <summary>
        /// The state the hook's notification has to land on. Without a mapping it would fall to
        /// Unknown and be DROPPED — the notification would arrive and change nothing at all.
        /// </summary>
        [Fact]
        public void ask_user_question_maps_to_a_question_block()
        {
            Assert.Equal(AttentionState.BlockedQuestion, AgentAttentionService.MapState("ask_user_question"));
        }

        [Fact]
        public void An_ask_user_question_notification_blocks_the_card()
        {
            var svc = new AgentAttentionService();

            Assert.True(svc.ApplyNotification(Payload("ask_user_question", "Alice asked: Pick one")));

            var e = svc.Get(Session);
            Assert.Equal(AttentionState.BlockedQuestion, e.State);
            Assert.True(e.IsBlocking);
            Assert.Contains("Pick one", e.Detail, StringComparison.Ordinal);
        }

        // ──────────── the idle timer must not clear a live block ────────────

        /// <summary>
        /// THE ONE THAT MATTERS, and the one that would have made the fix look correct and then
        /// fail intermittently in real use.
        /// <para>
        /// <c>ApplyNotification</c> assigned <c>e.State</c> unconditionally, and Claude Code emits
        /// <c>idle_prompt</c> on a TIMER — so one can land while the Owner simply has not answered
        /// yet. Applied, it rewrites BlockedQuestion to Idle and the card reads "Finished and idle"
        /// beside a question still on screen. That is the Owner's exact report.
        /// </para>
        /// </summary>
        [Fact]
        public void An_idle_prompt_does_not_clear_a_question_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question"));

            bool applied = svc.ApplyNotification(Payload("idle_prompt"));

            Assert.False(applied);
            Assert.Equal(AttentionState.BlockedQuestion, svc.Get(Session).State);
        }

        /// <summary>
        /// The same protection for a permission block. The guard is about "is this card blocking",
        /// not about which flavour of block it is — a fix that special-cased only questions would
        /// leave the identical lie reachable one notification type over.
        /// </summary>
        [Fact]
        public void An_idle_prompt_does_not_clear_a_permission_block_either()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("permission_prompt"));

            svc.ApplyNotification(Payload("idle_prompt"));

            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        /// <summary>
        /// THE COUNTERWEIGHT, without which the guard above is indistinguishable from "blocks are
        /// permanent". An idle_prompt on a card that is NOT blocking still means what it always
        /// meant: the agent finished its turn. Suppressing it there would make Idle unreachable by
        /// notification and leave every finished agent looking busy.
        /// </summary>
        [Fact]
        public void An_idle_prompt_still_sets_idle_on_a_card_that_is_not_blocking()
        {
            var svc = new AgentAttentionService();

            Assert.True(svc.ApplyNotification(Payload("idle_prompt")));

            Assert.Equal(AttentionState.Idle, svc.Get(Session).State);
        }

        /// <summary>
        /// The guard is deliberately narrow — Idle over a blocking state, nothing else. A real
        /// clear still arrives from observed activity or TURN_END, which are evidence the agent
        /// MOVED. If this went red, a block could outlive the work that resolved it.
        /// </summary>
        [Fact]
        public void Observed_activity_still_clears_a_question_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question"));

            svc.NoteObservedActivity(
                Session,
                DateTime.UtcNow.AddSeconds(5),
                isSubagent: false,
                activitySummary: "Bash: git status");

            var e = svc.Get(Session);
            Assert.Equal(AttentionState.Working, e.State);
            Assert.False(e.IsBlocking);
        }

        /// <summary>
        /// A turn genuinely ending clears it too. TURN_END is the Escape edge — the Owner dismissed
        /// the prompt rather than answering it — and must keep working through a question block.
        /// </summary>
        [Fact]
        public void A_turn_ending_still_clears_a_question_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question"));

            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false);

            Assert.False(svc.Get(Session).IsBlocking);
        }

        /// <summary>
        /// A SECOND question re-blocks a card the Owner had already answered. The per-block
        /// acknowledgement machinery (42052f0c) depends on every blocking notification counting as
        /// a new block; a question that failed to restamp would inherit the previous ack and sit
        /// silent.
        /// </summary>
        [Fact]
        public void A_second_question_after_a_clear_blocks_again()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question"));
            svc.NoteTurnEnded(Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false);
            Assert.False(svc.Get(Session).IsBlocking);

            Assert.True(svc.ApplyNotification(Payload("ask_user_question", "second question")));

            Assert.Equal(AttentionState.BlockedQuestion, svc.Get(Session).State);
        }

        // ─────── a vaguer block must not talk away a precise one (live test 1) ───────

        /// <summary>
        /// THE SECOND FAILURE THE LIVE TEST FOUND, and the one the tests above could not have
        /// caught because every one of them applies a SINGLE notification to a card.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured on 2026-09-03: Claude Code emits its OWN <c>permission_prompt</c> Notification
        /// about six seconds after every <c>AskUserQuestion</c> — 15:09:58.101 and 15:11:38.518,
        /// 6.2s and 6.1s after the two questions that preceded them. It arrives second, and
        /// <c>e.State = state</c> was unconditional between blocking states, so it won.
        /// </para>
        /// <para>
        /// The Owner watched this happen and described it exactly: the card said "asking a
        /// question" and "then it went right to 'needs permission'". Which is a lie with teeth —
        /// it sends them off to approve something, while the agent is holding a multiple-choice
        /// question that approving nothing will ever answer.
        /// </para>
        /// <para>
        /// If this goes red, the rail has resumed relabelling questions as permission requests.
        /// Fix the service, not the test.
        /// </para>
        /// </remarks>
        [Fact]
        public void The_permission_prompt_claude_code_sends_after_a_question_does_not_relabel_the_card()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question", "Alice asked: Pick one"));

            bool applied = svc.ApplyNotification(Payload("permission_prompt", "Alice needs permission to continue"));

            Assert.False(applied);
            var e = svc.Get(Session);
            Assert.Equal(AttentionState.BlockedQuestion, e.State);
            Assert.Contains("Pick one", e.Detail, StringComparison.Ordinal);
        }

        /// <summary>
        /// The flattened value has the least provenance of all, so it must not overwrite either
        /// real flavour. Reaching BlockedUnknown from a question would replace "Asked you a
        /// question" with "Waiting on you" — a downgrade to a shrug.
        /// </summary>
        [Fact]
        public void The_flattened_block_does_not_overwrite_a_question_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question"));

            svc.ApplyNotification(Payload("permission_request"));

            Assert.Equal(AttentionState.BlockedQuestion, svc.Get(Session).State);
        }

        /// <summary>
        /// COUNTERWEIGHT 1, and the one that stops the guard meaning "the first flavour a card ever
        /// sees wins forever". A permission_prompt on a card that is not blocking is the only
        /// evidence there is about that card, and it must still be believed.
        /// </summary>
        [Fact]
        public void A_permission_prompt_still_blocks_a_card_that_is_not_blocking()
        {
            var svc = new AgentAttentionService();

            Assert.True(svc.ApplyNotification(Payload("permission_prompt")));

            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        /// <summary>
        /// COUNTERWEIGHT 2: the guard is a one-way ratchet on CERTAINTY, not on order of arrival.
        /// A question landing on a permission block is MORE information than the card had, so it
        /// applies. Suppressing it would freeze whichever notification happened to arrive first.
        /// </summary>
        [Fact]
        public void A_question_still_upgrades_a_permission_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("permission_prompt"));

            Assert.True(svc.ApplyNotification(Payload("ask_user_question", "Alice asked: Pick one")));

            Assert.Equal(AttentionState.BlockedQuestion, svc.Get(Session).State);
        }

        /// <summary>
        /// COUNTERWEIGHT 3: a repeat at the SAME certainty is not a downgrade, so it still restamps
        /// as a new block. The per-block acknowledgement machinery (42052f0c) counts on that — a
        /// second permission prompt that failed to restamp would inherit the first one's ack and
        /// sit silent while the owner is genuinely being waited on.
        /// </summary>
        [Fact]
        public void A_second_permission_prompt_still_counts_as_a_new_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("permission_prompt"));
            long first = svc.Get(Session).BlockSeq;

            svc.ApplyNotification(Payload("permission_prompt"));

            Assert.NotEqual(first, svc.Get(Session).BlockSeq);
        }

        /// <summary>
        /// COUNTERWEIGHT 4, the important one: the guard must not make a FLAVOUR permanent. Once
        /// the agent moves, the question block is gone, and the next permission prompt is believed
        /// on its own terms. This is the whole reason the guard is scoped to "over a live block"
        /// rather than to the card's history.
        /// </summary>
        [Fact]
        public void After_activity_clears_the_question_a_permission_prompt_is_believed_again()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question"));
            svc.NoteObservedActivity(
                Session,
                DateTime.UtcNow.AddSeconds(5),
                isSubagent: false,
                activitySummary: "Bash: git status");

            Assert.True(svc.ApplyNotification(Payload("permission_prompt")));

            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        // ───────── the card must show the QUESTION, not the last tool ─────────

        private static readonly DateTime Now = new DateTime(2026, 9, 3, 22, 11, 0, DateTimeKind.Utc);

        private static AgentAttentionEntry Blocked(AttentionState state, string detail) =>
            new AgentAttentionEntry
            {
                SessionId = Session,
                AgentName = Agent,
                State = state,
                EnteredAtUtc = Now.AddSeconds(-21),
                Detail = detail,
                LastActivity = "Bash: HOOK=$(ls \"C:/Users/…/marketplaces\"",
                LastActivityAtUtc = Now.AddSeconds(-32),
            };

        /// <summary>
        /// The Owner's third observation from live test 1: "it still has the 'bash' message which
        /// is not relevant now."
        /// </summary>
        /// <remarks>
        /// <para>
        /// The detail line prefers LIVE activity, deliberately (task edcdcdd5) — a frozen sentence
        /// standing where a live observation belongs is how a 27-minute-old line got read as
        /// current. That preference is right nearly everywhere and is left alone.
        /// </para>
        /// <para>
        /// A question block is the exception, because there the notification message IS the
        /// question — the literal thing the owner is being asked. The last command the agent ran
        /// before asking is not an answer to "what do you want from me?", and putting it in the one
        /// line the owner reads wastes the only sentence the card gets.
        /// </para>
        /// </remarks>
        [Fact]
        public void A_question_block_shows_the_question_not_the_last_tool_that_ran()
        {
            var cards = MultiTerminal.AttentionPanel.AttentionCardProjector.Project(
                new[] { Blocked(AttentionState.BlockedQuestion, "Alice asked: Pick one") },
                null,
                null,
                Now);

            var card = Assert.Single(cards);
            Assert.Equal("Alice asked: Pick one", card.ObservedDetail);
        }

        /// <summary>
        /// <c>DetailIsLive</c> must follow the SAME decision, never be computed beside it. Left at
        /// "has live activity" it would stamp "seen 32s ago" onto a question that was never an
        /// observation — reintroducing the fossil-as-live lie from the opposite direction, which is
        /// the exact defect the flag was added to prevent.
        /// <para>
        /// False is also the honest answer here: a blocked agent has done nothing since asking, so
        /// the view's "from the alert, not observed since" is literally true.
        /// </para>
        /// </summary>
        [Fact]
        public void The_question_is_not_reported_as_a_live_observation()
        {
            var cards = MultiTerminal.AttentionPanel.AttentionCardProjector.Project(
                new[] { Blocked(AttentionState.BlockedQuestion, "Alice asked: Pick one") },
                null,
                null,
                Now);

            Assert.False(Assert.Single(cards).DetailIsLive);
        }

        /// <summary>
        /// THE COUNTERWEIGHT for the detail change. Every other card keeps preferring live
        /// activity — without this, the edit above is indistinguishable from reverting edcdcdd5 and
        /// putting stale notification text back on every card in the rail.
        /// </summary>
        [Fact]
        public void A_permission_block_still_shows_the_live_activity_line()
        {
            var cards = MultiTerminal.AttentionPanel.AttentionCardProjector.Project(
                new[] { Blocked(AttentionState.BlockedPermission, "Alice needs permission to continue") },
                null,
                null,
                Now);

            var card = Assert.Single(cards);
            Assert.StartsWith("Bash:", card.ObservedDetail, StringComparison.Ordinal);
            Assert.True(card.DetailIsLive);
        }

        /// <summary>
        /// A question block with no message must still fall back to the live activity rather than
        /// rendering an empty line. The card is shouting either way; showing nothing next to it is
        /// the one outcome with no value at all.
        /// </summary>
        [Fact]
        public void A_question_block_with_no_message_falls_back_to_live_activity()
        {
            var cards = MultiTerminal.AttentionPanel.AttentionCardProjector.Project(
                new[] { Blocked(AttentionState.BlockedQuestion, null) },
                null,
                null,
                Now);

            Assert.StartsWith("Bash:", Assert.Single(cards).ObservedDetail, StringComparison.Ordinal);
        }
    }
}
