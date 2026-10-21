using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the post-compaction continue (ticket c0ebcfd4): <see cref="CompactContinueGate"/> (only an
    /// agent-requested compaction continues, once), <see cref="CompactContinueSequencer"/> (never type
    /// while the agent is working; verify a turn started; at most one extra Enter) and the runner's
    /// row classification. Falsification runs are recorded in the ticket notes, each with the facts
    /// expected to go red before the run.
    /// </summary>
    public class CompactContinueTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        private static readonly CompactContinueTiming Timing = new CompactContinueTiming
        {
            Settle = TimeSpan.FromSeconds(1),
            Quiet = TimeSpan.FromSeconds(4),
            MaxIdleWait = TimeSpan.FromSeconds(60),
            Verify = TimeSpan.FromSeconds(10),
        };

        private static DateTime At(double seconds) => T0.AddSeconds(seconds);

        // ── Gate ─────────────────────────────────────────────────────────────

        [Fact]
        public void An_unarmed_pane_does_not_continue()
        {
            Assert.False(new CompactContinueGate().TryConsume(T0));
        }

        [Fact]
        public void An_armed_pane_continues_exactly_once()
        {
            var gate = new CompactContinueGate();
            gate.Arm(T0);

            Assert.True(gate.TryConsume(At(30)));
            Assert.False(gate.TryConsume(At(31)));
        }

        [Fact]
        public void An_expired_request_does_not_continue_and_is_cleared()
        {
            var gate = new CompactContinueGate();
            gate.Arm(T0);

            Assert.False(gate.TryConsume(T0 + CompactContinueGate.Expiry + TimeSpan.FromSeconds(1)));
            Assert.False(gate.TryConsume(T0 + CompactContinueGate.Expiry + TimeSpan.FromSeconds(2)));
        }

        // ── Idle ─────────────────────────────────────────────────────────────

        [Fact]
        public void Nothing_is_typed_before_the_settle_time_even_after_a_turn_end()
        {
            var s = new CompactContinueSequencer(T0, Timing);
            s.Observe(CompactContinueSignal.TurnEnded, At(0.2));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(0.5)));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(1)));
            Assert.Equal(CompactContinueTypeReason.TurnEnded, s.TypeReason);
        }

        [Fact]
        public void No_activity_at_all_types_on_the_quiet_edge_and_not_before()
        {
            var s = new CompactContinueSequencer(T0, Timing);

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(3.9)));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(4)));
            Assert.Equal(CompactContinueTypeReason.Quiet, s.TypeReason);
        }

        [Fact]
        public void A_working_agent_is_never_typed_into_until_its_turn_ends()
        {
            var s = new CompactContinueSequencer(T0, Timing);
            s.Observe(CompactContinueSignal.Working, At(2));

            // Past the quiet window: activity was seen, so quiet does not apply.
            Assert.Equal(CompactContinueStep.Wait, s.Next(At(10)));
            Assert.Equal(CompactContinueStep.Wait, s.Next(At(30)));

            s.Observe(CompactContinueSignal.TurnEnded, At(31));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(31.5)));
            Assert.Equal(CompactContinueTypeReason.TurnEnded, s.TypeReason);
        }

        [Fact]
        public void Work_after_a_turn_end_means_busy_again()
        {
            var s = new CompactContinueSequencer(T0, Timing);
            s.Observe(CompactContinueSignal.TurnEnded, At(0.5));
            s.Observe(CompactContinueSignal.Working, At(0.8));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(5)));
        }

        [Fact]
        public void A_turn_that_never_ends_is_typed_into_at_the_bounded_wait()
        {
            var s = new CompactContinueSequencer(T0, Timing);
            s.Observe(CompactContinueSignal.Working, At(2));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(59)));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(60)));
            Assert.Equal(CompactContinueTypeReason.GaveUpWaiting, s.TypeReason);
        }

        // ── Verify ───────────────────────────────────────────────────────────

        [Fact]
        public void Activity_after_typing_verifies_without_a_second_enter()
        {
            var s = TypedAt(5);
            s.Observe(CompactContinueSignal.Working, At(8));

            Assert.Equal(CompactContinueStep.Verified, s.Next(At(9)));
        }

        [Fact]
        public void Silence_after_typing_sends_enter_once_then_gives_up_without_a_third()
        {
            var s = TypedAt(5);

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(14.9)));
            Assert.Equal(CompactContinueStep.SendEnterAgain, s.Next(At(15)));
            s.NoteEnterResent(At(15));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(24.9)));
            Assert.Equal(CompactContinueStep.NotVerified, s.Next(At(25)));
            Assert.Equal(CompactContinueStep.NotVerified, s.Next(At(100)));
        }

        [Fact]
        public void Activity_after_the_second_enter_verifies()
        {
            var s = TypedAt(5);
            Assert.Equal(CompactContinueStep.SendEnterAgain, s.Next(At(15)));
            s.NoteEnterResent(At(15));
            s.Observe(CompactContinueSignal.TurnEnded, At(18));

            Assert.Equal(CompactContinueStep.Verified, s.Next(At(19)));
        }

        private static CompactContinueSequencer TypedAt(double seconds)
        {
            var s = new CompactContinueSequencer(T0, Timing);
            Assert.Equal(CompactContinueStep.Type, s.Next(At(seconds)));
            s.NoteTyped(At(seconds));
            return s;
        }

        // ── Row classification ───────────────────────────────────────────────

        private static ActivityFeedEntry Row(string actor, string type, string details = "{\"agent_id\":null}")
            => new ActivityFeedEntry { Id = 1, Actor = actor, ActivityType = type, DetailsJson = details, Timestamp = T0 };

        [Fact]
        public void Rows_are_classified_for_this_agents_main_thread_only()
        {
            Assert.Equal(CompactContinueSignal.TurnEnded, CompactContinueRunner.Classify(Row("Alice", "TURN_END"), "Alice"));
            Assert.Equal(CompactContinueSignal.Working, CompactContinueRunner.Classify(Row("alice", "TOOL_START"), "Alice"));
            Assert.Null(CompactContinueRunner.Classify(Row("Bob", "TOOL_START"), "Alice"));
            Assert.Null(CompactContinueRunner.Classify(Row("Alice", "TOOL_COMPLETE", "{\"agent_id\":\"sub-1\"}"), "Alice"));
            Assert.Null(CompactContinueRunner.Classify(Row("Alice", "PLANNING"), "Alice"));
        }

        // ── Runner end to end, fake clock and feed ───────────────────────────

        private sealed class FakeFeed : IActivityFeedReader
        {
            public readonly List<ActivityFeedEntry> Rows = new List<ActivityFeedEntry>();

            public long GetMaxActivityId() => Rows.Count == 0 ? 0 : Rows[^1].Id;

            public List<ActivityFeedEntry> GetActivitiesAfterId(long afterId, int limit = 200)
                => Rows.FindAll(r => r.Id > afterId);

            public void Add(string actor, string type)
                => Rows.Add(new ActivityFeedEntry { Id = Rows.Count + 1, Actor = actor, ActivityType = type, DetailsJson = "{\"agent_id\":null}", Timestamp = T0 });
        }

        [Fact]
        public async Task Runner_waits_out_a_busy_turn_types_once_and_verifies()
        {
            var feed = new FakeFeed();
            feed.Add("Alice", "TURN_END"); // before the compaction: history, below the watermark
            DateTime now = T0;
            var typedAt = new List<DateTime>();
            int enters = 0;

            var runner = new CompactContinueRunner(
                feed,
                "Alice",
                paneAlive: () => true,
                type: _ => { typedAt.Add(now); return Task.CompletedTask; },
                sendEnter: () => { enters++; return Task.FromResult(true); },
                log: null,
                utcNow: () => now,
                delay: d =>
                {
                    now += d;
                    if (now == T0.AddSeconds(2)) feed.Add("Alice", "TOOL_START");
                    if (now == T0.AddSeconds(8)) feed.Add("Alice", "TURN_END");
                    if (typedAt.Count == 1 && now == typedAt[0].AddSeconds(3)) feed.Add("Alice", "TOOL_START");
                    return Task.CompletedTask;
                },
                timing: Timing);

            CompactContinueStep result = await runner.RunAsync();

            Assert.Equal(CompactContinueStep.Verified, result);
            Assert.Single(typedAt);
            Assert.True(typedAt[0] >= T0.AddSeconds(8), $"typed at +{(typedAt[0] - T0).TotalSeconds}s, while the agent was still working");
            Assert.Equal(0, enters);
        }
    }
}
