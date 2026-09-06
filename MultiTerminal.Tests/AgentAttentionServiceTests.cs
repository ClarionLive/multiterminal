using System;
using System.Collections.Generic;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Coverage for the attention state machine (task 2289bb8a item 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state machine is the only part of the attention rail that can be tested without a
    /// deploy: the panel is WebView2 and an agent running inside MultiTerminal cannot deploy. So
    /// this is where the feature's correctness actually lives.
    /// </para>
    /// <para>
    /// Three of these tests pin findings from the item 0 spike, and each guards a failure that
    /// LOOKS FINE ON SCREEN. A rail that clears a live block renders a calm card, which is
    /// indistinguishable from nobody needing you — there is no error, no exception, and no way for
    /// the owner to tell. Those three are the ones worth defending:
    /// <see cref="Subagent_activity_never_clears_a_block"/>,
    /// <see cref="Activity_that_predates_the_block_does_not_clear_it"/>, and
    /// <see cref="Idle_prompt_is_not_a_block"/>.
    /// </para>
    /// </remarks>
    public class AgentAttentionServiceTests
    {
        private const string Session = "sess-1";

        private static Dictionary<string, object> Notification(
            string rawType, string toolUseId = null, string message = "why")
            => new Dictionary<string, object>
            {
                ["session_id"] = Session,
                ["agent_name"] = "Alice",
                ["notification_type"] = "permission_request",
                ["raw_type"] = rawType,
                ["tool_use_id"] = toolUseId,
                ["message"] = message,
            };

        // ─────────────────────────────────────────────────────────────────
        // The three that guard silent failures
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Measured in the item 0 spike: 12,544 of 64,444 PreToolUse events (19.5%) came from
        /// subagents, logged under the PARENT's session id. So "any activity means unblocked" is
        /// wrong about one time in five, and wrong in the direction that hides a waiting agent.
        /// If this test goes red the rail has started clearing live blocks — fix the service, not
        /// the test.
        /// </summary>
        [Fact]
        public void Subagent_activity_never_clears_a_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt"));

            bool changed = svc.NoteObservedActivity(Session, DateTime.UtcNow.AddMinutes(1), isSubagent: true);

            Assert.False(changed);
            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
            Assert.Equal(1, svc.BlockingCount);
        }

        /// <summary>
        /// A tool call already in flight when the block began drains afterwards and would otherwise
        /// look like "the owner answered". Ordering is the weakest form of evidence this service
        /// accepts, so it has to be applied strictly.
        /// </summary>
        [Fact]
        public void Activity_that_predates_the_block_does_not_clear_it()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt"));
            var blockedAt = svc.Get(Session).EnteredAtUtc;

            bool changed = svc.NoteObservedActivity(Session, blockedAt.AddSeconds(-5), isSubagent: false);

            Assert.False(changed);
            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        /// <summary>
        /// idle_prompt means the turn ended, not that the owner is being waited on. The hook
        /// flattens it into permission_request, which invites exactly the wrong reading — and an
        /// alert that fires for every agent that ever finished work is an alert nobody reads.
        /// </summary>
        [Fact]
        public void Idle_prompt_is_not_a_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("idle_prompt"));

            Assert.Equal(AttentionState.Idle, svc.Get(Session).State);
            Assert.False(svc.Get(Session).IsBlocking);
            Assert.Equal(0, svc.BlockingCount);
        }

        // ─────────────────────────────────────────────────────────────────
        // Set edge
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The two raw types are distinct states, not interchangeable flavours of "blocked".
        /// </summary>
        /// <remarks>
        /// ONE CARD PER FLAVOUR, DELIBERATELY (task ee17f42d, live test 1). This used to apply both
        /// notifications to the SAME session in sequence, which asserted the intended thing and
        /// also — by accident, and unremarked — pinned "a permission_prompt landing on a question
        /// block wins". That is the Owner-reported bug: Claude Code emits its own permission_prompt
        /// ~6s after every AskUserQuestion, so under the old behaviour a card raised honestly as
        /// "Asked you a question" relabelled itself "Needs permission" while the question was still
        /// on screen.
        /// <para>
        /// Separate cards keep this test asserting what its NAME says — that the mapping
        /// distinguishes the two — without also asserting an overwrite rule it was never about.
        /// Sequential-arrival behaviour is now covered explicitly, and on purpose, in
        /// <c>AskUserQuestionBlockTests</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void Question_and_permission_are_distinct_states()
        {
            var asked = new AgentAttentionService();
            asked.ApplyNotification(Notification("elicitation_dialog"));
            Assert.Equal(AttentionState.BlockedQuestion, asked.Get(Session).State);

            var permission = new AgentAttentionService();
            permission.ApplyNotification(Notification("permission_prompt"));
            Assert.Equal(AttentionState.BlockedPermission, permission.Get(Session).State);
        }

        /// <summary>
        /// When raw_type is missing — an older hook, since it ships from a separate repository —
        /// the flattened value still proves the agent is blocked but not which flavour. That gets
        /// its own state rather than a guess at one of the real two.
        /// </summary>
        [Fact]
        public void Flattened_type_without_raw_type_is_blocking_but_unflavoured()
        {
            var svc = new AgentAttentionService();
            var payload = Notification(rawType: null);
            payload["raw_type"] = null;

            svc.ApplyNotification(payload);

            var entry = svc.Get(Session);
            Assert.Equal(AttentionState.BlockedUnknown, entry.State);
            Assert.True(entry.IsBlocking);
        }

        /// <summary>An unrecognised type is not evidence; it must not overwrite a real block.</summary>
        [Fact]
        public void Unrecognised_type_leaves_the_previous_state_intact()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt"));

            bool changed = svc.ApplyNotification(Notification("auth_success"));

            Assert.False(changed);
            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        [Fact]
        public void Notification_with_no_session_or_agent_is_dropped()
        {
            var svc = new AgentAttentionService();

            bool changed = svc.ApplyNotification(new Dictionary<string, object>
            {
                ["raw_type"] = "permission_prompt",
            });

            Assert.False(changed);
            Assert.Empty(svc.Snapshot());
        }

        // ─────────────────────────────────────────────────────────────────
        // Clear edge
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Main_thread_activity_after_the_block_clears_it()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt"));

            bool changed = svc.NoteObservedActivity(Session, DateTime.UtcNow.AddSeconds(30), isSubagent: false);

            Assert.True(changed);
            Assert.Equal(AttentionState.Working, svc.Get(Session).State);
            Assert.Equal(0, svc.BlockingCount);
        }

        /// <summary>
        /// A DENIED permission produces PostToolUseFailure rather than PostToolUse. This service
        /// deliberately does not distinguish success from failure — both mean the owner answered.
        /// A clear path that accepted only success would leave every denial pulsing forever.
        /// </summary>
        [Fact]
        public void A_denied_permission_clears_just_like_an_approved_one()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt", toolUseId: "toolu_1"));

            // The caller passes the same shape for PostToolUse and PostToolUseFailure.
            bool changed = svc.NoteObservedActivity(
                Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false, toolUseId: "toolu_1");

            Assert.True(changed);
            Assert.Equal(AttentionState.Working, svc.Get(Session).State);
        }

        /// <summary>
        /// When both sides carry an id the match is by identity, so a DIFFERENT call resolving
        /// while this one is still pending must not clear it.
        /// </summary>
        [Fact]
        public void A_different_tool_use_id_does_not_clear_the_pending_one()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt", toolUseId: "toolu_pending"));

            bool changed = svc.NoteObservedActivity(
                Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false, toolUseId: "toolu_other");

            Assert.False(changed);
            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        /// <summary>
        /// Identity matching is only available when BOTH sides have an id. Whether Claude Code
        /// supplies tool_use_id on a Notification is still unproven, so the degraded path — order
        /// alone — has to work rather than silently never clearing.
        /// </summary>
        [Fact]
        public void Without_a_pending_id_ordering_alone_still_clears()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt", toolUseId: null));

            bool changed = svc.NoteObservedActivity(
                Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false, toolUseId: "toolu_whatever");

            Assert.True(changed);
            Assert.Equal(AttentionState.Working, svc.Get(Session).State);
        }

        // ─────────────────────────────────────────────────────────────────
        // The age clock
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// "How long has this been waiting" is the number that tells the owner which agent is worst
        /// stuck. Re-stamping it whenever the detail text is rewritten would reset a six-minute wait
        /// to zero, and the card would still look entirely plausible.
        /// </summary>
        [Fact]
        public void Rewriting_the_detail_does_not_restart_the_age_clock()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt", message: "first"));
            var firstSeen = svc.Get(Session).EnteredAtUtc;

            svc.ApplyNotification(Notification("permission_prompt", message: "second"));

            Assert.Equal("second", svc.Get(Session).Detail);
            Assert.Equal(firstSeen, svc.Get(Session).EnteredAtUtc);
        }

        [Fact]
        public void A_real_state_change_does_restart_the_age_clock()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt"));
            var blockedAt = svc.Get(Session).EnteredAtUtc;

            svc.NoteObservedActivity(Session, DateTime.UtcNow.AddSeconds(5), isSubagent: false);

            Assert.True(svc.Get(Session).EnteredAtUtc >= blockedAt);
            Assert.Equal(AttentionState.Working, svc.Get(Session).State);
        }

        // ─────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Going_offline_clears_a_pending_block()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("elicitation_dialog"));

            svc.MarkOffline(Session);

            Assert.Equal(AttentionState.Offline, svc.Get(Session).State);
            Assert.Equal(0, svc.BlockingCount);
        }

        // ─────────────────────────────────────────────────────────────────
        // Project, learned from the notification (item 3)
        // ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Project_and_cwd_are_learned_from_the_notification()
        {
            var svc = new AgentAttentionService();
            var payload = Notification("permission_prompt");
            payload["project_name"] = "MultiTerminal";
            payload["cwd"] = @"H:\DevLaptop\ClarionPowerShell\MultiTerminal";

            svc.ApplyNotification(payload);

            Assert.Equal("MultiTerminal", svc.Get(Session).Project);
            Assert.Equal(@"H:\DevLaptop\ClarionPowerShell\MultiTerminal", svc.Get(Session).Cwd);
        }

        /// <summary>
        /// The hook resolves the project by reading .claude/project.json from the session's cwd, and
        /// legitimately comes back empty for a directory that has none. Letting a later empty value
        /// overwrite a known one would make the card's project name flicker away mid-session, for a
        /// reason no one watching could work out.
        /// </summary>
        [Fact]
        public void A_later_payload_without_a_project_does_not_blank_the_known_one()
        {
            var svc = new AgentAttentionService();
            var first = Notification("permission_prompt");
            first["project_name"] = "MultiTerminal";
            svc.ApplyNotification(first);

            var second = Notification("elicitation_dialog");
            second["project_name"] = "";
            svc.ApplyNotification(second);

            Assert.Equal("MultiTerminal", svc.Get(Session).Project);
            Assert.Equal(AttentionState.BlockedQuestion, svc.Get(Session).State);
        }

        [Fact]
        public void Snapshot_is_a_copy_and_cannot_mutate_the_service()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(Notification("permission_prompt"));

            svc.Snapshot()[0].State = AttentionState.Working;

            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
        }

        /// <summary>
        /// A repeated NON-blocking notification is a no-op and must stay quiet.
        /// </summary>
        /// <remarks>
        /// This is the half of the original assertion that was actually protecting something: an
        /// agent whose state has not moved should not spam the panel with repaints.
        /// <para>
        /// It used to assert the same for a repeated <c>permission_prompt</c>, and that half was
        /// wrong — see <see cref="A_repeated_blocking_notification_is_a_new_block_and_must_fire"/>
        /// for why (task 42052f0c, pipeline run 3).
        /// </para>
        /// </remarks>
        [Fact]
        public void AttentionChanged_stays_quiet_on_a_non_blocking_no_op()
        {
            var svc = new AgentAttentionService();
            int fired = 0;
            svc.AttentionChanged += (s, e) => fired++;

            svc.ApplyNotification(Notification("idle_prompt", message: "same"));
            Assert.Equal(1, fired);

            svc.ApplyNotification(Notification("idle_prompt", message: "same"));
            Assert.Equal(1, fired);
        }

        /// <summary>
        /// A repeated BLOCKING notification is a new block, and must fire even though every
        /// displayed field is identical.
        /// </summary>
        /// <remarks>
        /// This test previously asserted the opposite, and that assertion encoded the defect rather
        /// than guarding against it (task 42052f0c, pipeline run 3).
        /// <para>
        /// Two permission prompts for the same tool are indistinguishable: same message, both
        /// <c>tool_use_id</c>s null — Claude Code does not send one on a Notification, confirmed
        /// from the live presence-only diagnostic added for task 2289bb8a item 0 — and, while the
        /// polled clear edge is still outstanding, the same state and activity line too. Treating
        /// that as a no-op means the panel is never told, so the acknowledgement the owner gave the
        /// FIRST prompt silently covers the second and a live alarm renders calm. The panel is
        /// repainted only by this event; there is no timer that would catch up later.
        /// </para>
        /// <para>
        /// The cost of being wrong the other way is one extra repaint and an alarm that shouts
        /// again — visible, and dismissible with a click. This file's governing asymmetry already
        /// says which way to break that tie.
        /// </para>
        /// </remarks>
        [Fact]
        public void A_repeated_blocking_notification_is_a_new_block_and_must_fire()
        {
            var svc = new AgentAttentionService();
            int fired = 0;
            svc.AttentionChanged += (s, e) => fired++;

            svc.ApplyNotification(Notification("permission_prompt", message: "same"));
            Assert.Equal(1, fired);
            long first = svc.Get(Session).BlockSeq;

            svc.ApplyNotification(Notification("permission_prompt", message: "same"));
            Assert.Equal(2, fired);
            Assert.NotEqual(first, svc.Get(Session).BlockSeq);
        }

        // ─────────────────────────────────────────────────────────────────
        // One live terminal, exactly one card (task cafd47b9)
        // ─────────────────────────────────────────────────────────────────

        private static Dictionary<string, object> NotificationFor(
            string sessionId, string agent, string rawType = "permission_prompt")
            => new Dictionary<string, object>
            {
                ["session_id"] = sessionId,
                ["agent_name"] = agent,
                ["notification_type"] = "permission_request",
                ["raw_type"] = rawType,
                ["message"] = "why",
            };

        /// <summary>
        /// THE INVARIANT. The owner reasons in terminals, not sessions: two terminals must produce
        /// two cards however many times either one has rotated its session.
        /// <para>
        /// This is the bug as reported — <c>/clear</c> in Diana's terminal mints a new session id,
        /// and before the fix the pre-clear entry was orphaned under the old key with no living
        /// agent to ever move it. The owner saw three cards for two terminals, one of them pulsing
        /// "needs permission" 49 minutes after that session had ceased to exist.
        /// </para>
        /// If this goes red, the rail has started accumulating corpses again.
        /// </summary>
        [Fact]
        public void Rotating_a_session_never_grows_the_card_count()
        {
            var svc = new AgentAttentionService();

            svc.ApplyNotification(NotificationFor("alice-1", "Alice"));
            svc.ApplyNotification(NotificationFor("diana-1", "Diana"));
            Assert.Equal(2, svc.Snapshot().Count);

            // Diana runs /clear repeatedly. Each rotation is a brand-new session id.
            for (int i = 2; i <= 6; i++)
            {
                svc.ApplyNotification(NotificationFor("diana-" + i, "Diana"));
                Assert.Equal(2, svc.Snapshot().Count);
            }

            var snapshot = svc.Snapshot();
            Assert.Equal("diana-6", snapshot.Single(e => e.AgentName == "Diana").SessionId);
            Assert.Equal("alice-1", snapshot.Single(e => e.AgentName == "Alice").SessionId);
        }

        /// <summary>
        /// The clear path has to supersede too. A terminal that is /cleared and then simply gets
        /// back to work never blocks, so <c>ApplyNotification</c> is never reached — if only that
        /// path superseded, the most common rotation of all would still leave a ghost.
        /// </summary>
        [Fact]
        public void Activity_on_a_rotated_session_also_supersedes_its_predecessor()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(NotificationFor("diana-1", "Diana"));

            svc.NoteObservedActivity(
                "diana-2", DateTime.UtcNow, isSubagent: false, toolUseId: null, agentName: "Diana");

            var snapshot = svc.Snapshot();
            Assert.Single(snapshot);
            Assert.Equal("diana-2", snapshot[0].SessionId);
            Assert.Equal(AttentionState.Working, snapshot[0].State);
        }

        /// <summary>
        /// An entry that arrived with no agent name has no established terminal identity. Letting a
        /// blank name match would turn unattributable input into a delete-everything primitive —
        /// one malformed payload would clear the whole rail, silently, and the owner's evidence
        /// that anyone needed them would be gone.
        /// </summary>
        [Fact]
        public void A_blank_agent_name_evicts_nothing()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(NotificationFor("alice-1", "Alice"));
            svc.ApplyNotification(NotificationFor("diana-1", "Diana"));

            var anonymous = new Dictionary<string, object>
            {
                ["session_id"] = "ghost-1",
                ["agent_name"] = "",
                ["raw_type"] = "permission_prompt",
                ["message"] = "who am I",
            };
            svc.ApplyNotification(anonymous);

            Assert.Equal(3, svc.Snapshot().Count);
        }

        /// <summary>
        /// Superseding must not become a back door around the age rule. <c>EnteredAtUtc</c> is the
        /// one number telling the owner which agent has been stuck longest; re-stamping a survivor
        /// because some OTHER terminal rotated would reset "waiting 40m" to "waiting 0s".
        /// </summary>
        [Fact]
        public void Superseding_one_agent_does_not_restart_another_agents_clock()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(NotificationFor("alice-1", "Alice"));
            DateTime aliceEntered = svc.Get("alice-1").EnteredAtUtc;

            svc.ApplyNotification(NotificationFor("diana-1", "Diana"));
            svc.ApplyNotification(NotificationFor("diana-2", "Diana"));

            Assert.Equal(aliceEntered, svc.Get("alice-1").EnteredAtUtc);
        }

        /// <summary>
        /// The panel rebuilds from <see cref="AgentAttentionService.Snapshot"/>, so a silent
        /// eviction leaves the ghost on screen until some unrelated event happens to fire. The
        /// removal must announce itself.
        /// </summary>
        [Fact]
        public void An_eviction_raises_AttentionRemoved_carrying_the_dropped_session()
        {
            var svc = new AgentAttentionService();
            var removed = new List<AgentAttentionEntry>();
            svc.AttentionRemoved += (s, e) => removed.Add(e);

            svc.ApplyNotification(NotificationFor("diana-1", "Diana"));
            Assert.Empty(removed);

            svc.ApplyNotification(NotificationFor("diana-2", "Diana"));

            Assert.Single(removed);
            Assert.Equal("diana-1", removed[0].SessionId);
            Assert.Equal("Diana", removed[0].AgentName);
        }

        /// <summary>
        /// Two agents are two terminals. Superseding is scoped to one agent and must never reach
        /// across — the whole point is that Alice keeps her card while Diana rotates hers.
        /// </summary>
        [Fact]
        public void Superseding_is_scoped_to_one_agent()
        {
            var svc = new AgentAttentionService();
            svc.ApplyNotification(NotificationFor("alice-1", "Alice"));
            svc.ApplyNotification(NotificationFor("diana-1", "Diana"));
            svc.ApplyNotification(NotificationFor("diana-2", "Diana"));

            Assert.NotNull(svc.Get("alice-1"));
            Assert.Null(svc.Get("diana-1"));
            Assert.NotNull(svc.Get("diana-2"));
        }
    }
}
