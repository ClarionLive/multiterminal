using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Covers <see cref="HelperReadinessTrigger"/> (task 7806024f) — the rule that decides when a spawned
    /// helper's job is typed into its pane.
    /// </summary>
    public class HelperReadinessTriggerTests
    {
        private const string Awaited = "SpawnProbe";

        /// <summary>
        /// ⚠️ THE LOAD-BEARING FACT. <c>TerminalRegistered</c> fires TWICE per spawn — first for MT's own
        /// pre-registration, which has no channel port because <c>claude</c> has not started, and again for
        /// the helper's real registration. A trigger keyed on "a registration happened" fires on the first
        /// and types the job into an empty pane, which is strictly worse than the 120s wait this change
        /// exists to remove: slow is recoverable, typing into nothing loses the job.
        /// </summary>
        [Fact]
        public void The_pre_registration_that_carries_no_channel_port_does_not_trigger()
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(Awaited, null, Awaited));
        }

        [Fact]
        public void A_registration_carrying_a_channel_port_triggers()
        {
            Assert.True(HelperReadinessTrigger.IsHelperAlive(Awaited, 8801, Awaited));
        }

        /// <summary>
        /// Another helper booting in the same MultiTerminal must not deliver THIS helper's job. Spawning
        /// two at once is ordinary, and their registrations interleave.
        /// </summary>
        [Fact]
        public void Another_helpers_registration_does_not_trigger()
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive("SomeoneElse", 8802, Awaited));
        }

        /// <summary>
        /// A held name comes back suffixed, and delivery binds to the RESOLVED identity. Waiting on the
        /// requested name instead would wait for a registration that never arrives — the orphan-pane
        /// defect class from 77d1182f item 8.
        /// </summary>
        [Fact]
        public void The_suffixed_identity_is_a_different_helper()
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive("SpawnProbe-2", 8802, Awaited));
            Assert.True(HelperReadinessTrigger.IsHelperAlive("SpawnProbe-2", 8802, "SpawnProbe-2"));
        }

        /// <summary>
        /// The registration crosses a process boundary from a component on its own release cadence, so a
        /// casing or whitespace variant must not silently degrade delivery back to the 120s path.
        /// </summary>
        [Theory]
        [InlineData("spawnprobe")]
        [InlineData("SPAWNPROBE")]
        [InlineData("  SpawnProbe  ")]
        public void Name_matching_tolerates_casing_and_whitespace(string registeredName)
        {
            Assert.True(HelperReadinessTrigger.IsHelperAlive(registeredName, 8801, Awaited));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_nameless_registration_does_not_trigger(string registeredName)
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(registeredName, 8801, Awaited));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void An_unnamed_awaited_helper_never_matches(string awaitedName)
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(Awaited, 8801, awaitedName));
        }

        /// <summary>
        /// A port of zero is still a port as far as the row is concerned; the predicate is "has the helper
        /// registered", not "is the port in a sensible range". Pinned so the null-vs-zero distinction is a
        /// decision rather than an accident.
        /// </summary>
        [Fact]
        public void A_zero_port_still_counts_as_registered()
        {
            Assert.True(HelperReadinessTrigger.IsHelperAlive(Awaited, 0, Awaited));
        }
    }
}
