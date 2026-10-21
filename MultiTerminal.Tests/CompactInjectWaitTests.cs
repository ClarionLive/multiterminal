using System;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins <see cref="CompactInjectWait"/> (ticket c0ebcfd4, live TEST 4): an agent-requested
    /// <c>/compact</c> is typed only into an empty, quiet input line, and dropped rather than typed
    /// into a line that never clears. The MainForm loop that polls it is executed live only.
    /// </summary>
    public class CompactInjectWaitTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Empty_line_nobody_ever_typed_injects_at_once()
        {
            Assert.Equal(CompactInjectStep.Inject, CompactInjectWait.Decide(false, null, T0, T0));
        }

        [Fact]
        public void Text_in_the_line_waits_even_when_the_typing_was_long_ago()
        {
            // TEST 4: "aga" sat in the line and the /compact was typed after it.
            Assert.Equal(CompactInjectStep.Wait, CompactInjectWait.Decide(true, T0.AddMinutes(-10), T0, T0.AddSeconds(30)));
        }

        [Fact]
        public void Empty_line_waits_until_the_typing_has_been_quiet_long_enough()
        {
            DateTime lastKey = T0.AddSeconds(5);
            Assert.Equal(CompactInjectStep.Wait, CompactInjectWait.Decide(false, lastKey, T0, lastKey + CompactInjectWait.Quiet - TimeSpan.FromMilliseconds(1)));
            Assert.Equal(CompactInjectStep.Inject, CompactInjectWait.Decide(false, lastKey, T0, lastKey + CompactInjectWait.Quiet));
        }

        [Fact]
        public void A_line_that_never_clears_is_dropped_not_typed_into()
        {
            Assert.Equal(CompactInjectStep.Wait, CompactInjectWait.Decide(true, T0, T0, T0 + CompactInjectWait.MaxWait - TimeSpan.FromMilliseconds(1)));
            Assert.Equal(CompactInjectStep.GiveUp, CompactInjectWait.Decide(true, T0, T0, T0 + CompactInjectWait.MaxWait));
        }

        [Fact]
        public void A_clear_quiet_line_injects_even_after_the_wait_limit()
        {
            Assert.Equal(CompactInjectStep.Inject, CompactInjectWait.Decide(false, T0, T0, T0 + CompactInjectWait.MaxWait + TimeSpan.FromSeconds(1)));
        }
    }
}
