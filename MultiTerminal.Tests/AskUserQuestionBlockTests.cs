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

            svc.ApplyNotification(Payload("permission_prompt", "Alice needs permission to continue"));

            // The WORDS are what the guard protects. Note it does not also assert "nothing
            // changed": an earlier version did, and that assertion was the ack-inheritance bug
            // written down as a requirement — see
            // A_suppressed_downgrade_still_counts_as_a_new_block for the other half of the
            // contract, which is that the block identity DOES move.
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

        private static AgentAttentionEntry Blocked(
            AttentionState state, string detail, bool detailIsQuestionText = false) =>
            new AgentAttentionEntry
            {
                SessionId = Session,
                AgentName = Agent,
                State = state,
                EnteredAtUtc = Now.AddSeconds(-21),
                Detail = detail,
                DetailIsQuestionText = detailIsQuestionText,
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
                new[] { Blocked(AttentionState.BlockedQuestion, "Alice asked: Pick one", detailIsQuestionText: true) },
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
                new[] { Blocked(AttentionState.BlockedQuestion, "Alice asked: Pick one", detailIsQuestionText: true) },
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
                new[] { Blocked(AttentionState.BlockedQuestion, null, detailIsQuestionText: true) },
                null,
                null,
                Now);

            Assert.StartsWith("Bash:", Assert.Single(cards).ObservedDetail, StringComparison.Ordinal);
        }

        // ═══════════ pipeline run 1 findings ═══════════

        private static Dictionary<string, object> PayloadKeyed(
            string rawType, string sessionId, string message = "msg") =>
            new Dictionary<string, object>
            {
                ["session_id"] = sessionId,
                ["agent_name"] = Agent,
                ["notification_type"] = "permission_request",
                ["raw_type"] = rawType,
                ["message"] = message,
            };

        /// <summary>
        /// THE ONE THAT PROVES THE GUARD RUNS AT ALL. Every test above shares one session constant,
        /// and that shared constant was the assumption that made a correct guard inert.
        /// </summary>
        /// <remarks>
        /// In production the two notifications did NOT share a key. `ask-user-relay-hook.js` sent an
        /// empty session_id — it read an env var Claude Code does not export to hook children —
        /// so the question was keyed by AGENT NAME, while `notification-hook.js` keyed its
        /// permission_prompt by the real uuid. The guard's lookup missed every time, a second card
        /// was created, and supersede evicted the question. The Owner saw the relabel exactly as
        /// before the fix.
        /// <para>
        /// If this goes red, the guard has gone back to trusting that two hooks in two repositories
        /// agree about a key. Fix the lookup, not the test.
        /// </para>
        /// </remarks>
        [Fact]
        public void A_permission_prompt_under_a_different_key_still_cannot_relabel_the_question()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(PayloadKeyed("ask_user_question", "", "Alice asked: Pick one"));

            svc.ApplyNotification(PayloadKeyed("permission_prompt", "uuid-1", "Alice needs permission"));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal(AttentionState.BlockedQuestion, card.State);
            Assert.Contains("Pick one", card.Detail, StringComparison.Ordinal);
        }

        /// <summary>
        /// And exactly ONE card survives it. The pre-fix failure was not only a relabel: the
        /// second key minted a second entry, so the terminal briefly owned two cards.
        /// </summary>
        [Fact]
        public void A_suppressed_downgrade_leaves_one_card_not_two()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(PayloadKeyed("ask_user_question", ""));

            svc.ApplyNotification(PayloadKeyed("permission_prompt", "uuid-1"));

            Assert.Single(svc.Snapshot());
        }

        /// <summary>
        /// THE ACKNOWLEDGEMENT CONTRACT — the finding that made the first attempt at this guard
        /// unshippable, and the reason suppression is not a silent no-op.
        /// </summary>
        /// <remarks>
        /// The panel silences an alarm by (session id, state, blockSeq), and `BlockSeq` advances
        /// only inside `UpsertLocked`. A guard that returned early preserved the truer WORDS while
        /// letting an owner who had acknowledged the QUESTION also, invisibly, acknowledge a real
        /// permission block that arrived afterwards — a calm card in front of a waiting agent,
        /// which is the one failure this rail exists to prevent.
        /// <para>
        /// So a suppressed downgrade must still count as a new block. This asserts the identity
        /// moved; the test above asserts the wording did not. Neither alone is the contract.
        /// </para>
        /// </remarks>
        [Fact]
        public void A_suppressed_downgrade_still_counts_as_a_new_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question"));
            long acknowledgedBlock = svc.Get(Session).BlockSeq;

            bool announced = svc.ApplyNotification(Payload("permission_prompt"));

            Assert.True(announced);
            Assert.NotEqual(acknowledgedBlock, svc.Get(Session).BlockSeq);
        }

        /// <summary>
        /// The untested rung. The ranking is a three-value ladder and the tests above only pin
        /// 2-under-3 and 1-under-3 — a ranking of {3, 2, 2} would satisfy all of them.
        /// </summary>
        [Fact]
        public void The_flattened_block_does_not_overwrite_a_permission_block_either()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("permission_prompt"));

            svc.ApplyNotification(Payload("permission_request"));

            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        /// <summary>
        /// PARITY SWEEP. `BlockCertainty` is a third enumeration of the blocking states, beside the
        /// two `IsBlocking` lists that carry a comment about not drifting apart.
        /// </summary>
        /// <remarks>
        /// The drift here fails SILENTLY and in the dangerous direction: a future blocking state
        /// added to both `IsBlocking` lists but missed in `BlockCertainty` falls to `default: 0`,
        /// which ranks below every real block — so it would be suppressed over any live block and
        /// the owner would simply never be told. A dropped alarm, arrived at by omission.
        /// </remarks>
        [Fact]
        public void Every_blocking_state_has_a_certainty_rank()
        {
            foreach (AttentionState state in Enum.GetValues(typeof(AttentionState)))
            {
                if (!AttentionStates.IsBlocking(state)) continue;

                Assert.True(
                    AttentionStates.BlockCertainty(state) > 0,
                    $"{state} is blocking but has no certainty rank, so it would be silently "
                    + "suppressed over any live block. Add it to BlockCertainty.");
            }
        }

        /// <summary>
        /// A question block reached from `elicitation_dialog` keeps its live activity line.
        /// </summary>
        /// <remarks>
        /// Both raw types land on `BlockedQuestion`, but only `ask_user_question` carries the
        /// agent's actual words. `elicitation_dialog` gets the notification hook's fixed string,
        /// "Alice has a question that needs your response" — which restates the card's own verb.
        /// Preferring THAT over a real observation would be a worse card reached by the same
        /// reasoning that makes the question case a better one, which is why the projector keys on
        /// provenance rather than on state.
        /// </remarks>
        [Fact]
        public void An_elicitation_dialog_block_keeps_its_live_activity_line()
        {
            var cards = MultiTerminal.AttentionPanel.AttentionCardProjector.Project(
                new[]
                {
                    Blocked(
                        AttentionState.BlockedQuestion,
                        "Alice has a question that needs your response",
                        detailIsQuestionText: false),
                },
                null,
                null,
                Now);

            var card = Assert.Single(cards);
            Assert.StartsWith("Bash:", card.ObservedDetail, StringComparison.Ordinal);
            Assert.True(card.DetailIsLive);
        }

        /// <summary>
        /// The provenance flag is set from the raw type, beside the Detail it describes — so an
        /// `ask_user_question` notification is what makes the projector prefer its message.
        /// </summary>
        [Fact]
        public void Only_ask_user_question_marks_its_detail_as_question_text()
        {
            var asked = new AgentAttentionService();
            asked.ApplyNotification(Payload("ask_user_question", "Alice asked: Pick one"));
            Assert.True(asked.Get(Session).DetailIsQuestionText);

            var elicited = new AgentAttentionService();
            elicited.ApplyNotification(Payload("elicitation_dialog", "Alice has a question"));
            Assert.False(elicited.Get(Session).DetailIsQuestionText);
        }

        // ═══════════ pipeline run 2 findings ═══════════

        /// <summary>
        /// THE WORSE HALF OF THE SAME BUG, and the one three separate gates found independently.
        /// </summary>
        /// <remarks>
        /// The certainty guard got the cross-key lookup; its sibling idle guard did not. Under the
        /// identical key split, an <c>idle_prompt</c> carrying the real uuid misses the guard, Idle
        /// is written under that key, and supersede evicts the name-keyed question — so the card
        /// reads "Finished and idle" in front of an agent that is waiting.
        /// <para>
        /// That is not a smaller failure than the relabel this ticket started with. It is the
        /// SENTENCE this ticket was filed about, reached through a different door. A relabel is a
        /// lie the Owner can see; silence is one they cannot.
        /// </para>
        /// <para>
        /// Claude Code emits <c>idle_prompt</c> on a timer, so the second half of this sequence is
        /// the routine case, not a contrivance.
        /// </para>
        /// </remarks>
        [Fact]
        public void An_idle_prompt_under_a_different_key_still_cannot_clear_the_question()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(PayloadKeyed("ask_user_question", "", "Alice asked: Pick one"));
            long blockBefore = Assert.Single(svc.Snapshot()).BlockSeq;

            svc.ApplyNotification(PayloadKeyed("idle_prompt", "uuid-1"));

            var card = Assert.Single(svc.Snapshot());
            Assert.Equal(AttentionState.BlockedQuestion, card.State);
            Assert.Contains("Pick one", card.Detail, StringComparison.Ordinal);

            // An idle timer is not new evidence, so it must not restamp the block either — that
            // would re-shout an alarm the Owner had already acknowledged, for nothing.
            Assert.Equal(blockBefore, card.BlockSeq);
        }

        /// <summary>
        /// COUNTERWEIGHT: the idle guard's cross-key lookup must not make Idle unreachable. A card
        /// that is not blocking still accepts an idle_prompt arriving under any key.
        /// </summary>
        [Fact]
        public void An_idle_prompt_under_a_different_key_still_sets_idle_when_nothing_is_blocking()
        {
            var svc = new AgentAttentionService();

            Assert.True(svc.ApplyNotification(PayloadKeyed("idle_prompt", "uuid-1")));

            Assert.Equal(AttentionState.Idle, svc.Get("uuid-1").State);
        }

        /// <summary>
        /// Provenance says the stored string IS a question. It does not say the agent is STILL
        /// waiting on one, and the two are not the same claim.
        /// </summary>
        /// <remarks>
        /// <c>MarkOffline</c> changes only the state, leaving Detail and the provenance flag in
        /// place — so a card reading "Disconnected" would have displaced its live activity line
        /// with a question nobody can answer any more. The state test is an AND with the provenance
        /// test, not something the provenance test replaced.
        /// </remarks>
        [Fact]
        public void A_disconnected_card_does_not_still_show_the_question()
        {
            var offline = Blocked(AttentionState.Offline, "Alice asked: Pick one", detailIsQuestionText: true);

            var cards = MultiTerminal.AttentionPanel.AttentionCardProjector.Project(
                new[] { offline }, null, null, Now);

            var card = Assert.Single(cards);
            Assert.StartsWith("Bash:", card.ObservedDetail, StringComparison.Ordinal);
            Assert.True(card.DetailIsLive);
        }

        /// <summary>
        /// The provenance flag is cleared WHERE Detail is cleared, not merely where it is set.
        /// Its own doc says it must not outlive the string it describes; this is that sentence
        /// made executable rather than aspirational.
        /// </summary>
        [Fact]
        public void Clearing_a_question_block_also_clears_its_provenance()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Payload("ask_user_question", "Alice asked: Pick one"));
            Assert.True(svc.Get(Session).DetailIsQuestionText);

            svc.NoteObservedActivity(
                Session,
                DateTime.UtcNow.AddSeconds(5),
                isSubagent: false,
                activitySummary: "Bash: git status");

            var e = svc.Get(Session);
            Assert.Null(e.Detail);
            Assert.False(e.DetailIsQuestionText);
        }
    }
}
