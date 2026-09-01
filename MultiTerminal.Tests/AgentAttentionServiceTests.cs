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

        [Fact]
        public void Question_and_permission_are_distinct_states()
        {
            var svc = new AgentAttentionService();

            svc.ApplyNotification(Notification("elicitation_dialog"));
            Assert.Equal(AttentionState.BlockedQuestion, svc.Get(Session).State);

            svc.ApplyNotification(Notification("permission_prompt"));
            Assert.Equal(AttentionState.BlockedPermission, svc.Get(Session).State);
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

        [Fact]
        public void AttentionChanged_fires_on_change_and_stays_quiet_on_a_no_op()
        {
            var svc = new AgentAttentionService();
            int fired = 0;
            svc.AttentionChanged += (s, e) => fired++;

            svc.ApplyNotification(Notification("permission_prompt", message: "same"));
            Assert.Equal(1, fired);

            svc.ApplyNotification(Notification("permission_prompt", message: "same"));
            Assert.Equal(1, fired);
        }
    }
}
