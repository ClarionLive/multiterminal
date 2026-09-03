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
    }
}
