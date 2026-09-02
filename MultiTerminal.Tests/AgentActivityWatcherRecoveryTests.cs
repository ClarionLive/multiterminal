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

        private static ActivityFeedEntry MainThreadRow(long id, string type, string summary) => new ActivityFeedEntry
        {
            Id = id,
            Timestamp = DateTime.UtcNow,
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
        /// Priming late must still mean "from now on". Rows that existed when the watcher finally
        /// managed to prime are history, exactly as they would have been had the first read worked.
        /// </summary>
        [Fact]
        public void A_late_prime_still_starts_at_the_current_maximum()
        {
            var feed = new FlakyFeed { MaxIdFailuresRemaining = 1 };
            feed.Rows.Add(MainThreadRow(7, "TOOL_COMPLETE", "Edit: Old.cs"));
            feed.MaxId = 7;
            var attention = new AgentAttentionService();
            using var watcher = new AgentActivityWatcher(feed, attention);

            Assert.False(watcher.Prime());
            watcher.Poll();

            Assert.True(watcher.IsPrimed);
            Assert.Null(attention.GetByAgent("Alice"));

            feed.Rows.Add(MainThreadRow(8, "TOOL_COMPLETE", "Edit: New.cs"));
            watcher.Poll();
            Assert.Equal("Edit: New.cs", attention.GetByAgent("Alice")?.LastActivity);
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
