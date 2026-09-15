using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Covers <see cref="HelperReadinessTrigger"/> (task 7806024f) — the rule that decides when a spawned
    /// helper's job is typed into its pane.
    ///
    /// <para><b>⚠️ RE-KEYED FROM DISPLAY NAME TO DOCID (task c28e6177), and the facts below changed
    /// polarity because of it.</b> The predicate used to compare TRIMMED, case-insensitive display names.
    /// Nothing else in the system trims one: the broker's uniqueness scan and row lookup both compare
    /// ordinal-ignore-case untrimmed, as do the plugin channel server's roster lookup and
    /// <c>isAddressedToMe</c>. So a helper spawned as <c>"Alice "</c> while <c>"Alice"</c> was live got a
    /// SECOND broker row — unsuffixed, because the name was not held — and the live Alice's own
    /// registration then satisfied this predicate and delivered that helper's job.
    /// <c>HelperReadinessIdentityAsymmetryTests</c> reproduces that against a real broker.</para>
    ///
    /// <para>The parameters are now docIds: MT-minted <c>Guid</c>s, one per pane, and the SAME key
    /// <c>Deliver</c> uses to find the document it types into. Fixture values below are shaped like docIds
    /// rather than names on purpose — a test that keeps calling them "SpawnProbe" invites the next reader
    /// to reason about them as names and put the trim back.</para>
    /// </summary>
    public class HelperReadinessTriggerTests
    {
        /// <summary>The docId of the pane this spawn created — what <c>QueueInitialPromptDelivery</c> waits on.</summary>
        private const string AwaitedDocId = "7f3a9c21e5b44d0e8a1c6b2f9d4e7a05";

        /// <summary>A different pane's docId. Distinct in every character, as two Guids are.</summary>
        private const string OtherDocId = "1b8e4d72a0c94f36b5d2e7c18f0a63b9";

        /// <summary>
        /// ⚠️ THE LOAD-BEARING FACT. <c>TerminalRegistered</c> fires TWICE per spawn — first for MT's own
        /// pre-registration, which has no channel port because <c>claude</c> has not started, and again for
        /// the helper's real registration. A trigger keyed on "a registration happened" fires on the first
        /// and types the job into an empty pane, which is strictly worse than the 120s wait this change
        /// exists to remove: slow is recoverable, typing into nothing loses the job.
        /// <para>Note this fact is about the PORT and survives the docId re-key untouched — both raises
        /// carry the same docId, so the port is the only thing distinguishing them.</para>
        /// </summary>
        [Fact]
        public void The_pre_registration_that_carries_no_channel_port_does_not_trigger()
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(AwaitedDocId, null, AwaitedDocId));
        }

        [Fact]
        public void A_registration_carrying_a_channel_port_triggers()
        {
            Assert.True(HelperReadinessTrigger.IsHelperAlive(AwaitedDocId, 8801, AwaitedDocId));
        }

        /// <summary>
        /// Another helper booting in the same MultiTerminal must not deliver THIS helper's job. Spawning
        /// two at once is ordinary, and their registrations interleave.
        /// </summary>
        [Fact]
        public void Another_helpers_registration_does_not_trigger()
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(OtherDocId, 8802, AwaitedDocId));
        }

        /// <summary>
        /// The held-name hazard, which is now structural rather than checked. A requested name that is
        /// already taken comes back suffixed ("Name-2"), and a predicate bound to the REQUESTED name would
        /// wait for a registration that never arrives — the orphan-pane defect class from 77d1182f item 8.
        /// <para>A docId has no requested-versus-resolved distinction: <c>AddNewTerminal</c> mints it and
        /// returns the same value it pre-registered with. So the two panes of a name collision are simply
        /// two docIds, and this is the same fact as
        /// <see cref="Another_helpers_registration_does_not_trigger"/> — kept separate because the hazard
        /// it names is the reason anyone would be tempted to reintroduce name matching.</para>
        /// </summary>
        [Fact]
        public void A_second_pane_from_the_same_requested_name_is_a_different_helper()
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(OtherDocId, 8802, AwaitedDocId));
            Assert.True(HelperReadinessTrigger.IsHelperAlive(OtherDocId, 8802, OtherDocId));
        }

        /// <summary>
        /// ⚠️ THIS FACT REPLACED ITS OWN INVERSE, so the reversal is recorded rather than just performed.
        ///
        /// <para>The previous version was <c>Name_matching_tolerates_casing_and_whitespace</c>, a Theory
        /// with <c>"spawnprobe"</c>, <c>"SPAWNPROBE"</c> and <c>"  SpawnProbe  "</c>, all asserting TRUE.
        /// Its stated reason was that the registration crosses a process boundary from a component on its
        /// own release cadence, so a variant must not degrade delivery back to the 120s path. Two things
        /// were wrong with that. The whitespace case pinned the c28e6177 defect as required behaviour —
        /// it is the exact input that let one helper's job be delivered on another terminal's
        /// registration. And the cross-process premise was never checked: the producer is
        /// <c>multiterminal-channel.mjs</c>, which reads <c>process.env.MULTITERMINAL_NAME</c> with no
        /// trim and posts it verbatim, while MT sets that variable as a single-quoted PowerShell literal
        /// that preserves whitespace exactly. The tolerance was absorbing a variant nothing produced.</para>
        ///
        /// <para>Under a docId there is no such boundary at all: MT mints it, holds it, and compares it
        /// against its own pane. Tolerance is not merely unnecessary, it is harmful — <c>Deliver</c> finds
        /// the document with <c>t.DocId == docId</c> (ordinal, case-sensitive), so a predicate that
        /// accepted a case variant would approve a delivery the pane lookup then fails to find.</para>
        /// </summary>
        [Theory]
        [InlineData("7F3A9C21E5B44D0E8A1C6B2F9D4E7A05")]   // same docId, upper-cased
        [InlineData(" 7f3a9c21e5b44d0e8a1c6b2f9d4e7a05")]  // leading space
        [InlineData("7f3a9c21e5b44d0e8a1c6b2f9d4e7a05 ")]  // trailing space — the c28e6177 input
        public void DocId_matching_is_exact_and_tolerates_no_variants(string registeredDocId)
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(registeredDocId, 8801, AwaitedDocId));
        }

        /// <summary>
        /// A row with no docId is not "the pane with no docId" — it is a row this spawn has no
        /// relationship to. Adopted sessions register without one, so blank-matches-blank would fire the
        /// trigger for a terminal nobody spawned.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_registration_with_no_docid_does_not_trigger(string registeredDocId)
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(registeredDocId, 8801, AwaitedDocId));
            Assert.False(HelperReadinessTrigger.IsHelperAlive(registeredDocId, 8801, registeredDocId));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_spawn_with_no_docid_never_matches(string awaitedDocId)
        {
            Assert.False(HelperReadinessTrigger.IsHelperAlive(AwaitedDocId, 8801, awaitedDocId));
        }

        /// <summary>
        /// A port of zero is still a port as far as the row is concerned; the predicate is "has the helper
        /// registered", not "is the port in a sensible range". Pinned so the null-vs-zero distinction is a
        /// decision rather than an accident.
        /// </summary>
        [Fact]
        public void A_zero_port_still_counts_as_registered()
        {
            Assert.True(HelperReadinessTrigger.IsHelperAlive(AwaitedDocId, 0, AwaitedDocId));
        }
    }
}
