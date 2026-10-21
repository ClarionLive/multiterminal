using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the post-compaction continue (ticket c0ebcfd4): <see cref="CompactContinueGate"/> (only the
    /// requested, manual compaction continues, once; a failed submission withdraws the request),
    /// <see cref="CompactContinueSequencer"/> (never types into a working agent or blind on a failing
    /// feed; at most one extra Enter, and none when the silence cannot be trusted or someone typed) and
    /// the runner's row classification. Falsification runs are recorded in the ticket notes, each with
    /// the facts expected to go red written down before the run.
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
        public void An_unarmed_pane_does_not_continue_even_after_a_manual_compaction()
        {
            var gate = new CompactContinueGate();
            gate.NoteCompactionStarting(manual: true);

            Assert.False(gate.TryConsume(T0));
        }

        [Fact]
        public void An_armed_pane_continues_exactly_once_after_a_manual_compaction()
        {
            var gate = new CompactContinueGate();
            gate.Arm(T0);
            gate.NoteCompactionStarting(manual: true);

            Assert.True(gate.TryConsume(At(30)));
            Assert.False(gate.TryConsume(At(31)));
        }

        [Fact]
        public void An_automatic_compaction_does_not_take_the_request_and_leaves_it_for_the_manual_one()
        {
            var gate = new CompactContinueGate();
            gate.Arm(T0);

            gate.NoteCompactionStarting(manual: false);
            Assert.False(gate.TryConsume(At(20)));

            gate.NoteCompactionStarting(manual: true);
            Assert.True(gate.TryConsume(At(40)));
        }

        [Fact]
        public void A_compaction_with_no_trigger_report_does_not_take_the_request()
        {
            var gate = new CompactContinueGate();
            gate.Arm(T0);

            Assert.False(gate.TryConsume(At(20)));
        }

        [Fact]
        public void A_withdrawn_request_is_not_taken_by_a_later_manual_compaction()
        {
            var gate = new CompactContinueGate();
            gate.Arm(T0);
            gate.Disarm();
            gate.NoteCompactionStarting(manual: true);

            Assert.False(gate.TryConsume(At(20)));
        }

        [Fact]
        public void An_expired_request_does_not_continue()
        {
            var gate = new CompactContinueGate();
            gate.Arm(T0);
            gate.NoteCompactionStarting(manual: true);

            Assert.False(gate.TryConsume(T0 + CompactContinueGate.Expiry + TimeSpan.FromSeconds(1)));
        }

        // ── Idle ─────────────────────────────────────────────────────────────

        private static CompactContinueSequencer Healthy()
        {
            var s = new CompactContinueSequencer(T0, Timing);
            s.NoteFeedRead(true, T0);
            return s;
        }

        [Fact]
        public void Nothing_is_typed_before_the_settle_time_even_after_a_turn_end()
        {
            var s = Healthy();
            s.Observe(CompactContinueSignal.TurnEnded, At(0.2));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(0.5)));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(1)));
            Assert.Equal(CompactContinueTypeReason.TurnEnded, s.TypeReason);
        }

        [Fact]
        public void A_healthy_quiet_feed_types_on_the_quiet_edge_and_not_before()
        {
            var s = Healthy();

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(3.9)));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(4)));
            Assert.Equal(CompactContinueTypeReason.Quiet, s.TypeReason);
        }

        [Fact]
        public void A_feed_that_never_reads_is_never_quiet_and_gives_up_without_typing()
        {
            var s = new CompactContinueSequencer(T0, Timing);
            for (int t = 0; t < 60; t++)
            {
                s.NoteFeedRead(false, At(t));
                Assert.Equal(CompactContinueStep.Wait, s.Next(At(t + 0.5)));
            }

            Assert.Equal(CompactContinueStep.GaveUp, s.Next(At(60)));
            Assert.Equal(CompactContinueGiveUpReason.FeedUnavailable, s.GiveUpReason);
        }

        [Fact]
        public void A_failed_read_restarts_the_quiet_window()
        {
            var s = Healthy();
            s.NoteFeedRead(false, At(3));
            s.NoteFeedRead(true, At(3.5));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(7)));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(7.5)));
        }

        [Fact]
        public void A_turn_end_seen_before_a_failed_read_is_not_typed_on_until_a_read_succeeds()
        {
            var s = Healthy();
            s.Observe(CompactContinueSignal.TurnEnded, At(0.5));
            s.NoteFeedRead(false, At(2));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(2)));

            s.NoteFeedRead(true, At(2.5));
            Assert.Equal(CompactContinueStep.Type, s.Next(At(2.5)));
        }

        [Fact]
        public async Task Runner_drains_a_backlog_past_one_batch_before_deciding()
        {
            // A TURN_END followed by more than one batch of rows that end in work: a single 200-row
            // read would see only the TURN_END and type into a working agent.
            var feed = new FakeFeed();
            DateTime now = T0;
            var typedAt = new List<DateTime>();
            bool added = false;

            var runner = Runner(feed, () => now, d =>
            {
                now += d;
                if (!added && now >= At(1))
                {
                    added = true;
                    feed.Add("Alice", "TURN_END");
                    for (int i = 0; i < CompactContinueRunner.BatchSize + 5; i++) feed.Add("Bob", "TOOL_START");
                    feed.Add("Alice", "TOOL_START");
                }
            }, typedAt, () => { });

            Assert.Equal(CompactContinueStep.GaveUp, await runner.RunAsync());
            Assert.Empty(typedAt);
        }

        [Fact]
        public void A_working_agent_is_never_typed_into_and_is_given_up_on_at_the_cap()
        {
            var s = Healthy();
            s.Observe(CompactContinueSignal.Working, At(2));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(10)));
            Assert.Equal(CompactContinueStep.Wait, s.Next(At(59.5)));
            Assert.Equal(CompactContinueStep.GaveUp, s.Next(At(60)));
            Assert.Equal(CompactContinueGiveUpReason.StillWorking, s.GiveUpReason);
            Assert.Equal(CompactContinueStep.GaveUp, s.Next(At(61)));
        }

        [Fact]
        public void A_working_agent_is_typed_into_once_its_turn_ends()
        {
            var s = Healthy();
            s.Observe(CompactContinueSignal.Working, At(2));
            s.Observe(CompactContinueSignal.TurnEnded, At(31));

            Assert.Equal(CompactContinueStep.Type, s.Next(At(31.5)));
            Assert.Equal(CompactContinueTypeReason.TurnEnded, s.TypeReason);
        }

        [Fact]
        public void Work_after_a_turn_end_means_busy_again()
        {
            var s = Healthy();
            s.Observe(CompactContinueSignal.TurnEnded, At(0.5));
            s.Observe(CompactContinueSignal.Working, At(0.8));

            Assert.Equal(CompactContinueStep.Wait, s.Next(At(5)));
        }

        [Fact]
        public void Someone_typing_after_the_compaction_means_nothing_is_typed()
        {
            var s = Healthy();
            s.NoteUserInput(At(2));

            Assert.Equal(CompactContinueStep.GaveUp, s.Next(At(5)));
            Assert.Equal(CompactContinueGiveUpReason.OwnerTyping, s.GiveUpReason);
        }

        [Fact]
        public void Someone_typing_during_the_compaction_means_nothing_is_typed()
        {
            // The /compact was submitted 40s before the compaction finished; the input line was open.
            var s = new CompactContinueSequencer(T0, Timing, ownerInputSinceUtc: At(-40));
            s.NoteFeedRead(true, T0);
            s.NoteUserInput(At(-20));

            Assert.Equal(CompactContinueStep.GaveUp, s.Next(At(5)));
            Assert.Equal(CompactContinueGiveUpReason.OwnerTyping, s.GiveUpReason);
        }

        [Theory]
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'I' }, true)]                         // focus in
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'O' }, true)]                         // focus out
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'<', (byte)'0', (byte)';', (byte)'1', (byte)';', (byte)'1', (byte)'M' }, true)] // SGR mouse
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'M', 32, 33, 33 }, true)]             // X10 mouse
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~', (byte)'h', (byte)'i' }, false)] // bracketed paste
        [InlineData(new byte[] { 0x1B, (byte)'[', (byte)'A' }, false)]                        // Up arrow (history recall)
        [InlineData(new byte[] { (byte)'a' }, false)]
        [InlineData(new byte[] { 0x0D }, false)]
        public void Only_focus_and_mouse_reports_are_not_counted_as_someone_typing(byte[] data, bool isReport)
        {
            Assert.Equal(isReport, MultiTerminal.Controls.TerminalControl.IsTerminalReport(data));
        }

        [Fact]
        public void Keystrokes_from_before_the_compaction_finished_do_not_count()
        {
            var s = Healthy();
            s.NoteUserInput(At(-30));

            Assert.Equal(CompactContinueStep.Type, s.Next(At(4)));
        }

        // ── Verify ───────────────────────────────────────────────────────────

        private static CompactContinueSequencer TypedAt(double seconds)
        {
            var s = Healthy();
            Assert.Equal(CompactContinueStep.Type, s.Next(At(seconds)));
            s.NoteTyped(At(seconds));
            return s;
        }

        [Fact]
        public void Activity_after_typing_verifies_without_a_second_enter()
        {
            var s = TypedAt(5);
            s.Observe(CompactContinueSignal.Working, At(8));

            Assert.Equal(CompactContinueStep.Verified, s.Next(At(9)));
        }

        [Fact]
        public void Silence_after_typing_sends_enter_once_then_stops_without_a_third()
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
        public void No_second_enter_when_the_feed_failed_after_typing()
        {
            var s = TypedAt(5);
            s.NoteFeedRead(false, At(8));
            s.NoteFeedRead(true, At(9));

            Assert.Equal(CompactContinueStep.NotVerified, s.Next(At(15)));
        }

        [Fact]
        public void No_second_enter_when_someone_typed_after_the_prompt()
        {
            var s = TypedAt(5);
            s.NoteUserInput(At(7));

            Assert.Equal(CompactContinueStep.NotVerified, s.Next(At(15)));
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

        // ── Row classification ───────────────────────────────────────────────

        private static ActivityFeedEntry Row(string actor, string type, string details = "{\"agent_id\":null}")
            => new ActivityFeedEntry { Id = 1, Actor = actor, ActivityType = type, DetailsJson = details, Timestamp = T0 };

        [Fact]
        public void Rows_are_classified_for_this_agents_main_thread_only()
        {
            Assert.Equal(CompactContinueSignal.TurnEnded, CompactContinueRunner.Classify(Row("Alice", "TURN_END"), "Alice"));
            Assert.Equal(CompactContinueSignal.Working, CompactContinueRunner.Classify(Row("alice", "TOOL_START"), "Alice"));
            Assert.Equal(CompactContinueSignal.Working, CompactContinueRunner.Classify(Row("Alice", "TOOL_QUIET"), "Alice"));
            Assert.Equal(CompactContinueSignal.Working, CompactContinueRunner.Classify(Row("Alice", "BUILD_STARTED"), "Alice"));
            Assert.Null(CompactContinueRunner.Classify(Row("Bob", "TOOL_START"), "Alice"));
            Assert.Null(CompactContinueRunner.Classify(Row("Alice", "TOOL_COMPLETE", "{\"agent_id\":\"sub-1\"}"), "Alice"));
            Assert.Null(CompactContinueRunner.Classify(Row("Alice", "PLANNING"), "Alice"));
        }

        // ── Runner end to end, fake clock and feed ───────────────────────────

        private sealed class FakeFeed : IActivityFeedReader
        {
            public readonly List<ActivityFeedEntry> Rows = new List<ActivityFeedEntry>();
            public bool Broken;

            public long GetMaxActivityId()
            {
                if (Broken) throw new InvalidOperationException("database is locked");
                return Rows.Count == 0 ? 0 : Rows[^1].Id;
            }

            public List<ActivityFeedEntry> GetActivitiesAfterId(long afterId, int limit = 200)
            {
                if (Broken) throw new InvalidOperationException("database is locked");
                List<ActivityFeedEntry> after = Rows.FindAll(r => r.Id > afterId);
                return after.GetRange(0, Math.Min(limit, after.Count));
            }

            public void Add(string actor, string type)
                => Rows.Add(new ActivityFeedEntry { Id = Rows.Count + 1, Actor = actor, ActivityType = type, DetailsJson = "{\"agent_id\":null}", Timestamp = T0 });
        }

        private static CompactContinueRunner Runner(FakeFeed feed, Func<DateTime> now, Action<TimeSpan> advance, List<DateTime> typedAt, Action onEnter)
            => new CompactContinueRunner(
                feed,
                "Alice",
                paneAlive: () => true,
                lastUserInput: () => null,
                type: _ => { typedAt.Add(now()); return Task.CompletedTask; },
                sendEnter: () => { onEnter(); return Task.FromResult(true); },
                log: null,
                utcNow: now,
                delay: d => { advance(d); return Task.CompletedTask; },
                timing: Timing);

        [Fact]
        public async Task Runner_waits_out_a_busy_turn_types_once_and_verifies()
        {
            var feed = new FakeFeed();
            feed.Add("Alice", "TURN_END"); // before the compaction: history, below the watermark
            DateTime now = T0;
            var typedAt = new List<DateTime>();
            int enters = 0;

            var runner = Runner(feed, () => now, d =>
            {
                now += d;
                if (now == At(2)) feed.Add("Alice", "TOOL_START");
                if (now == At(8)) feed.Add("Alice", "TURN_END");
                if (typedAt.Count == 1 && now == typedAt[0].AddSeconds(3)) feed.Add("Alice", "TOOL_START");
            }, typedAt, () => enters++);

            CompactContinueStep result = await runner.RunAsync();

            Assert.Equal(CompactContinueStep.Verified, result);
            Assert.Single(typedAt);
            Assert.True(typedAt[0] >= At(8), $"typed at +{(typedAt[0] - T0).TotalSeconds}s, while the agent was still working");
            Assert.Equal(0, enters);
        }

        [Fact]
        public async Task Runner_on_a_broken_feed_types_nothing_and_presses_nothing()
        {
            var feed = new FakeFeed { Broken = true };
            DateTime now = T0;
            var typedAt = new List<DateTime>();
            int enters = 0;

            var runner = Runner(feed, () => now, d => now += d, typedAt, () => enters++);

            Assert.Equal(CompactContinueStep.GaveUp, await runner.RunAsync());
            Assert.Empty(typedAt);
            Assert.Equal(0, enters);
        }

        [Fact]
        public async Task Runner_without_a_feed_types_nothing()
        {
            var typedAt = new List<DateTime>();
            DateTime now = T0;
            var runner = new CompactContinueRunner(
                null, "Alice", () => true, () => null,
                _ => { typedAt.Add(now); return Task.CompletedTask; },
                () => Task.FromResult(true), null,
                utcNow: () => now, delay: d => { now += d; return Task.CompletedTask; }, timing: Timing);

            Assert.Equal(CompactContinueStep.GaveUp, await runner.RunAsync());
            Assert.Empty(typedAt);
        }
    }
}
