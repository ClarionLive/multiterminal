using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task <c>c28e6177</c>. Pins the disagreement between <see cref="HelperReadinessTrigger"/> and the
    /// broker about what counts as ONE terminal identity.
    ///
    /// <para><b>Why this file exists at all.</b> The ticket arrived as an INFERENCE from a code review —
    /// "the predicate trims, the broker's lookup path reportedly does not" — with nobody having
    /// reproduced it. A fix built on an unreproduced review is worse than no fix here, because the
    /// obvious-looking correction (trim on both sides) would MERGE two identities inside the comparison
    /// that routes messages between agents. So the disagreement is demonstrated first, against a real
    /// <see cref="MessageBroker"/> on a temp database, and only then fixed.</para>
    ///
    /// <para><b>What was found by reading, and is asserted below.</b> Four independent name comparisons
    /// agree on untrimmed ordinal-ignore-case — <c>FindUniqueCandidate</c> (MessageBroker.cs:1987),
    /// <c>GetTerminal</c> (MessageBroker.cs:3874), and two in the plugin's channel server
    /// (<c>multiterminal-channel.mjs</c> :98 roster lookup and :355 <c>isAddressedToMe</c>).
    /// <c>MessageBroker.cs</c> contains exactly two <c>.Trim()</c> calls in ~8K lines and neither is a
    /// name. <see cref="HelperReadinessTrigger"/> is the only site that trims, which is why IT is the
    /// side that moves.</para>
    ///
    /// <para><b>Reading the results.</b> Every fact here is green today EXCEPT
    /// <see cref="A_registration_for_one_row_must_not_satisfy_a_wait_for_a_different_row"/>, which is the
    /// defect and is expected RED until the predicate is re-keyed. The green ones establish the premise
    /// it depends on: that the broker really does mint two rows.</para>
    ///
    /// <para>⚠️ <b>The fix changes the predicate's signature</b> (display name → docId), so
    /// <see cref="RegistrationOfSatisfiesWaitFor"/> is the ONE line that will be rewritten. Every
    /// <c>Assert</c> below is phrased in terms of broker ROWS rather than the signature, so the facts
    /// survive the re-key unchanged — which is the point: what must stay true is "a registration for
    /// row X does not answer a wait for row Y", not any particular way of checking it.</para>
    ///
    /// Uses a REAL <see cref="MessageBroker"/> with its databases isolated to temp files, matching
    /// <c>BrokerDuplicateNameRejectionTests</c>.
    /// </summary>
    public sealed class HelperReadinessIdentityAsymmetryTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public HelperReadinessIdentityAsymmetryTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_identasym_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_identasym_msg_{stamp}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _dbPath, _msgDbPath })
            {
                foreach (var f in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }

            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// PREMISE, and it is the half the review could not check: the broker mints a SECOND terminal for
        /// a name that differs only by a trailing space. <c>FindUniqueCandidate</c>'s <c>IsHeld</c> does
        /// not trim, so "Alice " is not held by "Alice" and comes back UNSUFFIXED — the spawn proceeds
        /// with two connected rows whose names are indistinguishable on screen.
        /// </summary>
        [Fact]
        public void The_broker_mints_a_second_row_for_a_whitespace_variant_name()
        {
            using var broker = new MessageBroker();

            var alice = broker.RegisterTerminal("Alice", docId: "DOC-ALICE", channelPort: 8801, nonce: "N-ALICE");
            Assert.True(alice.Success);

            // A helper spawned as "Alice " — one trailing space, exactly what a phone keyboard adds.
            var helper = broker.RegisterTerminalUnique("Alice ", out string resolved, docId: "DOC-HELPER", nonce: "N-HELPER");
            Assert.True(helper.Success);

            // NOT "Alice-2". The uniqueness scan never saw a collision to suffix.
            Assert.Equal("Alice ", resolved);
            Assert.NotEqual(alice.TerminalId, helper.TerminalId);

            var aliceRow = broker.GetTerminal("Alice");
            var helperRow = broker.GetTerminal("Alice ");
            Assert.NotNull(aliceRow);
            Assert.NotNull(helperRow);
            Assert.NotEqual(aliceRow.Id, helperRow.Id);
            Assert.Equal("DOC-ALICE", aliceRow.DocId);
            Assert.Equal("DOC-HELPER", helperRow.DocId);
        }

        /// <summary>
        /// ⚠️ THE DEFECT. Expected RED until the predicate is re-keyed off display names.
        ///
        /// <para>Alice's registration — her row, her port, her pane — reports that a DIFFERENT terminal
        /// is alive and ready to be typed into. The two rows are the broker's own, taken straight from
        /// the test above, so this is not a claim about strings: it is a registration for one row
        /// answering a wait for another.</para>
        ///
        /// <para>What that costs is in <c>MainForm.QueueInitialPromptDelivery</c>: the handler calls
        /// <c>Deliver</c>, which takes the exactly-once guard and unsubscribes both other triggers. The
        /// helper's own registration, when it finally arrives, is then ignored.</para>
        /// </summary>
        [Fact]
        public void A_registration_for_one_row_must_not_satisfy_a_wait_for_a_different_row()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Alice", docId: "DOC-ALICE", channelPort: 8801, nonce: "N-ALICE");
            broker.RegisterTerminalUnique("Alice ", out _, docId: "DOC-HELPER", nonce: "N-HELPER");

            var aliceRow = broker.GetTerminal("Alice");
            var helperRow = broker.GetTerminal("Alice ");

            Assert.NotEqual(aliceRow.Id, helperRow.Id);
            Assert.False(
                RegistrationOfSatisfiesWaitFor(aliceRow, helperRow),
                "A registration for Alice's row satisfied the wait for the helper's row. The broker holds "
                + "two terminals here; the readiness predicate sees one.");
        }

        /// <summary>
        /// The other half of the same instant, and the reason the above is not merely early delivery:
        /// the helper's OWN row proves it is not alive. It has no channel port, because <c>claude</c> has
        /// not started in its pane — so the only thing that made the predicate say "alive" was a row
        /// belonging to someone else.
        /// </summary>
        [Fact]
        public void The_helper_is_provably_not_alive_at_the_moment_the_predicate_says_it_is()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Alice", docId: "DOC-ALICE", channelPort: 8801, nonce: "N-ALICE");
            broker.RegisterTerminalUnique("Alice ", out _, docId: "DOC-HELPER", nonce: "N-HELPER");

            var helperRow = broker.GetTerminal("Alice ");

            // MT's pre-registration carries no port; only the helper's real register_terminal sets one.
            Assert.Null(helperRow.ChannelPort);
            Assert.False(RegistrationOfSatisfiesWaitFor(helperRow, helperRow));
        }

        /// <summary>
        /// CONTROL — an exact name collision is handled correctly, and must keep being handled correctly.
        /// The broker suffixes it to "Bob-2" and the predicate agrees the two are different helpers
        /// (<c>HelperReadinessTriggerTests.The_suffixed_identity_is_a_different_helper</c>).
        /// <para>This is what localises the defect to whitespace rather than to name-matching in general,
        /// and it is the fact that fails if anyone "fixes" this ticket by making the broker trim: "Alice "
        /// would then suffix to "Alice-2" and the helper would lose the identity it was spawned under.</para>
        /// </summary>
        [Fact]
        public void An_exact_name_collision_is_suffixed_and_the_two_are_correctly_distinct()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Bob", docId: "DOC-BOB", channelPort: 8801, nonce: "N-BOB");
            var helper = broker.RegisterTerminalUnique("Bob", out string resolved, docId: "DOC-BOB2", nonce: "N-BOB2");

            Assert.True(helper.Success);
            Assert.Equal("Bob-2", resolved);

            var bobRow = broker.GetTerminal("Bob");
            var helperRow = broker.GetTerminal("Bob-2");
            Assert.NotEqual(bobRow.Id, helperRow.Id);
            Assert.False(RegistrationOfSatisfiesWaitFor(bobRow, helperRow));
        }

        /// <summary>
        /// CONTROL — casing is SYMMETRIC and therefore not a defect. Both sides compare
        /// ordinal-ignore-case, so a cased variant collides in the broker, is suffixed, and the predicate
        /// correctly calls the two different helpers.
        /// <para>Pinned because the fix deletes the predicate's whitespace tolerance and the casing
        /// tolerance sits in the same expression. Case-insensitivity is not part of this bug and must
        /// survive the fix.</para>
        /// </summary>
        [Fact]
        public void A_cased_variant_is_symmetric_and_is_not_the_defect()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Carol", docId: "DOC-CAROL", channelPort: 8801, nonce: "N-CAROL");
            var helper = broker.RegisterTerminalUnique("CAROL", out string resolved, docId: "DOC-CAROL2", nonce: "N-CAROL2");

            Assert.True(helper.Success);

            // The broker saw the collision precisely because its comparison ignores case.
            Assert.Equal("CAROL-2", resolved);

            var carolRow = broker.GetTerminal("Carol");
            var helperRow = broker.GetTerminal("CAROL-2");
            Assert.NotEqual(carolRow.Id, helperRow.Id);
            Assert.False(RegistrationOfSatisfiesWaitFor(carolRow, helperRow));
        }

        /// <summary>
        /// The question <c>MainForm.QueueInitialPromptDelivery</c>'s registration handler actually asks:
        /// "a registration just arrived for THIS row — does it mean the helper I am waiting for is alive?"
        /// <para>Modelled here as rows rather than strings so the facts outlive the re-key. Today the
        /// handler passes <c>row.Name</c> and the awaited display name (MainForm.cs:2378-2382); after the
        /// fix it passes docIds. Only this method changes.</para>
        /// </summary>
        private static bool RegistrationOfSatisfiesWaitFor(TerminalInfo registered, TerminalInfo awaited)
            => HelperReadinessTrigger.IsHelperAlive(registered.Name, registered.ChannelPort, awaited.Name);
    }
}
