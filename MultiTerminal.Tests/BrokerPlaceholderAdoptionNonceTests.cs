using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Security regression tests for the launch-nonce placeholder-adoption gate (task fd3437e6),
    /// covering the two orderings the cross-model adversary asked to be pinned:
    /// (1) a foreign registration that leaked a seeded placeholder's docId but has no nonce must NOT
    ///     adopt or squat it, and the real child (correct nonce) must still adopt — no owner lockout;
    /// (2) distinct-docId "Unassigned" placeholders each keep their OWN seeded broker row, and a
    ///     same-docId re-registration under the shared sentinel name reuses that exact row rather than
    ///     minting a duplicate unseeded one (the ambiguous-FirstOrDefault hole).
    /// Uses a REAL <see cref="MessageBroker"/> with its databases isolated to temp files.
    /// </summary>
    public sealed class BrokerPlaceholderAdoptionNonceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public BrokerPlaceholderAdoptionNonceTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_nonce_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_nonce_msg_{stamp}.db");
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

        [Fact]
        public void Foreign_registration_without_nonce_cannot_adopt_or_squat_and_real_child_still_adopts()
        {
            using var broker = new MessageBroker();

            // MT seeds the "Unassigned" placeholder for doc B with its launch nonce.
            broker.RegisterTerminal("Unassigned", docId: "DB", nonce: "NB");

            // A foreign registration presents doc B's leaked docId but no matching nonce → rejected.
            broker.RegisterTerminal("Foo", docId: "DB", nonce: null);

            var afterForeign = broker.GetTerminals();
            // The foreign name must NOT end up owning docId DB (neither adoption nor a squatting row).
            Assert.DoesNotContain(afterForeign, t => t.Name == "Foo" && t.DocId == "DB");
            // The seeded placeholder for DB survives intact with its nonce.
            Assert.Contains(afterForeign, t => t.Name == "Unassigned" && t.DocId == "DB" && t.LaunchNonce == "NB");
            // Exactly one row owns DB (the untouched placeholder) — no duplicate/hole was created.
            Assert.Single(afterForeign, t => t.DocId == "DB");

            // The REAL child echoes the matching nonce → adopts the placeholder.
            broker.RegisterTerminal("Bob", docId: "DB", nonce: "NB");

            var afterReal = broker.GetTerminals();
            Assert.Contains(afterReal, t => t.Name == "Bob" && t.DocId == "DB");
            Assert.Single(afterReal, t => t.DocId == "DB");
        }

        [Fact]
        public void Distinct_unassigned_placeholders_each_keep_their_own_seeded_row()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Unassigned", docId: "DA", nonce: "NA");
            broker.RegisterTerminal("Unassigned", docId: "DB", nonce: "NB");

            var terms = broker.GetTerminals();
            // Not collapsed onto one shared "Unassigned" row: each docId keeps its own seeded nonce.
            Assert.Contains(terms, t => t.DocId == "DA" && t.LaunchNonce == "NA");
            Assert.Contains(terms, t => t.DocId == "DB" && t.LaunchNonce == "NB");
            Assert.Single(terms, t => t.DocId == "DA");
            Assert.Single(terms, t => t.DocId == "DB");
        }

        [Fact]
        public void Same_docId_unassigned_reregistration_reuses_its_own_row_without_duplicate()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Unassigned", docId: "DA", nonce: "NA");
            broker.RegisterTerminal("Unassigned", docId: "DB", nonce: "NB");
            // Re-register the SAME docId under the shared sentinel name — the ambiguous-FirstOrDefault
            // case the anchor-to-docId fix must handle without minting a duplicate unseeded row.
            broker.RegisterTerminal("Unassigned", docId: "DB", nonce: "NB");

            var terms = broker.GetTerminals();
            Assert.Single(terms, t => t.DocId == "DB");
            Assert.Contains(terms, t => t.DocId == "DB" && t.LaunchNonce == "NB");
        }

        [Fact]
        public void Same_name_nonceless_registration_cannot_attach_to_a_seeded_placeholder()
        {
            using var broker = new MessageBroker();

            // MT seeds an "Unassigned" placeholder with a launch nonce (no channel port yet).
            broker.RegisterTerminal("Unassigned", docId: "DB", nonce: "NB");

            // Foreign caller re-registers under the SAME sentinel name with the leaked docId, NO
            // nonce, and tries to point the row's channel port (message delivery) at itself.
            broker.RegisterTerminal("Unassigned", docId: "DB", nonce: null, channelPort: 8801);

            var terms = broker.GetTerminals();
            // The seeded placeholder for DB is untouched: exactly one row owns DB, still nonce NB,
            // and its ChannelPort was NOT hijacked to the foreign 8801 (the foreign reg was forced
            // to a fresh isolated row with docId cleared).
            Assert.Single(terms, t => t.DocId == "DB");
            Assert.Contains(terms, t => t.DocId == "DB" && t.LaunchNonce == "NB");
            Assert.DoesNotContain(terms, t => t.DocId == "DB" && t.ChannelPort == 8801);

            // The real child (matching nonce) still adopts and sets its own channel port.
            broker.RegisterTerminal("Bob", docId: "DB", nonce: "NB", channelPort: 8802);

            var after = broker.GetTerminals();
            Assert.Single(after, t => t.DocId == "DB");
            Assert.Contains(after, t => t.DocId == "DB" && t.Name == "Bob" && t.ChannelPort == 8802);
        }
    }
}
