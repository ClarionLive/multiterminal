using System;
using System.Collections.Generic;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The watcher survives a feed that fails and then recovers (task edcdcdd5, pipeline run 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cross-model adversary found this one: <c>StartAgentActivityWatcher</c> assigned the
    /// field and then called <c>Start()</c>, and <c>Start()</c> returned quietly when the first
    /// watermark read threw. The field was non-null, so the idempotence guard refused every later
    /// start, and one transient SQLite "database is locked" at startup — which this app has a whole
    /// WriteContention diagnostics stream for — left the rail silently dead for the life of the
    /// process. Exactly the owner's original complaint, reproduced by a fix for it.
    /// </para>
    /// <para>
    /// The security gate found the sibling: a batch read that throws leaves the watermark where it
    /// was, so the same unreadable row is the first unread row on every tick, and everything behind
    /// it is starved. That one is fixed on the read side (a row is never unreadable); this file pins
    /// the watcher's half — a throwing read is logged and the next tick tries again.
    /// </para>
    /// </remarks>
    public class AgentActivityWatcherRecoveryTests
    {
        private sealed class FlakyFeed : IActivityFeedReader
        {
            public int MaxIdFailuresRemaining { get; set; }

            public int ReadFailuresRemaining { get; set; }

            public long MaxId { get; set; }

            public List<ActivityFeedEntry> Rows { get; } = new List<ActivityFeedEntry>();

            public int MaxIdCalls { get; private set; }

            public long GetMaxActivityId()
            {
                MaxIdCalls++;
                if (MaxIdFailuresRemaining-- > 0) throw new InvalidOperationException("database is locked");
                return MaxId;
            }

            public List<ActivityFeedEntry> GetActivitiesAfterId(long afterId, int limit = 200)
            {
                if (ReadFailuresRemaining-- > 0) throw new InvalidOperationException("database is locked");
                return Rows.FindAll(r => r.Id > afterId);
            }
        }

        private static ActivityFeedEntry MainThreadRow(long id, string type, string summary, DateTime? at = null) => new ActivityFeedEntry
        {
            Id = id,
            Timestamp = at ?? DateTime.UtcNow,
            ActivityType = type,
            Actor = "Alice",
            Summary = summary,
            DetailsJson = "{\"agent_id\":null}",
        };

        [Fact]
        public void A_failed_first_prime_does_not_kill_the_watcher()
        {
            var feed = new FlakyFeed { MaxIdFailuresRemaining = 1, MaxId = 0 };
            var attention = new AgentAttentionService();
            var log = new List<string>();
            using var watcher = new AgentActivityWatcher(feed, attention, log.Add);

            Assert.False(watcher.Prime());
            Assert.False(watcher.IsPrimed);

            // The next tick primes on its own and applies what it finds.
            feed.Rows.Add(MainThreadRow(1, "TOOL_COMPLETE", "Edit: A.cs"));
            watcher.Poll();

            Assert.True(watcher.IsPrimed);
            Assert.Contains(log, l => l.Contains("could not read the watermark", StringComparison.Ordinal));
            Assert.Equal("Edit: A.cs", attention.GetByAgent("Alice")?.LastActivity);
        }

        /// <summary>
        /// A late prime must not throw away the window it was blind for. Rows written BEFORE the
        /// watcher existed are history and stay unapplied; rows written while it was unprimed are
        /// live work — one of them may be the TOOL_COMPLETE that clears a block — and are applied
        /// once the prime finally succeeds. (Pipeline run 2, adversary finding.)
        /// </summary>
        [Fact]
        public void A_late_prime_replays_the_unprimed_window_but_not_history()
        {
            var feed = new FlakyFeed { MaxIdFailuresRemaining = 1 };
            feed.Rows.Add(MainThreadRow(7, "TOOL_COMPLETE", "Edit: Old.cs", DateTime.UtcNow.AddMinutes(-10)));
            feed.MaxId = 7;
            var attention = new AgentAttentionService();
            using var watcher = new AgentActivityWatcher(feed, attention);

            Assert.False(watcher.Prime());

            // Written while unprimed.
            feed.Rows.Add(MainThreadRow(8, "TOOL_COMPLETE", "Edit: DuringOutage.cs", DateTime.UtcNow.AddSeconds(1)));
            feed.MaxId = 8;

            watcher.Poll();

            Assert.True(watcher.IsPrimed);
            var e = attention.GetByAgent("Alice");
            Assert.NotNull(e);
            Assert.Equal("Edit: DuringOutage.cs", e.LastActivity);
            Assert.DoesNotContain("Old.cs", e.LastActivity, StringComparison.Ordinal);

            feed.Rows.Add(MainThreadRow(9, "TOOL_COMPLETE", "Edit: New.cs", DateTime.UtcNow.AddSeconds(2)));
            watcher.Poll();
            Assert.Equal("Edit: New.cs", attention.GetByAgent("Alice")?.LastActivity);
        }

        /// <summary>
        /// The normal path is untouched: a first-try prime starts at MAX and replays nothing.
        /// </summary>
        [Fact]
        public void A_first_try_prime_still_starts_at_the_current_maximum()
        {
            var feed = new FlakyFeed { MaxId = 7 };
            feed.Rows.Add(MainThreadRow(7, "TOOL_COMPLETE", "Edit: Old.cs", DateTime.UtcNow.AddSeconds(1)));
            var attention = new AgentAttentionService();
            using var watcher = new AgentActivityWatcher(feed, attention);

            Assert.True(watcher.Prime());
            watcher.Poll();

            Assert.Null(attention.GetByAgent("Alice"));
        }

        [Fact]
        public void Prime_is_idempotent_once_it_has_succeeded()
        {
            var feed = new FlakyFeed { MaxId = 3 };
            using var watcher = new AgentActivityWatcher(feed, new AgentAttentionService());

            Assert.True(watcher.Prime());
            Assert.True(watcher.Prime());
            watcher.Poll();

            Assert.Equal(1, feed.MaxIdCalls);
        }

        [Fact]
        public void A_throwing_batch_read_is_retried_on_the_next_tick()
        {
            var feed = new FlakyFeed { ReadFailuresRemaining = 1 };
            var attention = new AgentAttentionService();
            var log = new List<string>();
            using var watcher = new AgentActivityWatcher(feed, attention, log.Add);
            Assert.True(watcher.Prime());

            feed.Rows.Add(MainThreadRow(1, "TOOL_COMPLETE", "Edit: A.cs"));
            watcher.Poll();
            Assert.Null(attention.GetByAgent("Alice"));
            Assert.Contains(log, l => l.Contains("poll failed", StringComparison.Ordinal));

            watcher.Poll();
            Assert.Equal("Edit: A.cs", attention.GetByAgent("Alice")?.LastActivity);
        }
    }
}
