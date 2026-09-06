using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Security regression tests for the real-name duplicate-registration gate (task c9285d2a).
    ///
    /// <para>The hole this closes: gates (1)-(3) in <c>RegisterTerminal</c> all concern the
    /// "Unassigned" sentinel — gate (3) says so itself, "Real-named rows never reach here". So a
    /// registration for a REAL name that is already held by a connected terminal fell straight into
    /// the reuse branch with no proof of origin and OVERWROTE that terminal's ChannelPort. Since
    /// channel delivery is keyed on name alone (<c>multiterminal-channel.mjs</c> <c>isAddressedToMe</c>),
    /// that is impersonation: the claimant starts receiving the victim's pushed messages.</para>
    ///
    /// <para><b>The second test is the load-bearing one.</b> The same branch is how the channel server
    /// reports its own port and how its 30s drift heartbeat corrects it — a same-name registration
    /// carrying no docId. A gate that refuses those would kill push delivery for every terminal while
    /// <c>get_messages</c> polling kept working and hid the damage. That test is what stops the gate
    /// from being "simplified" into exactly that regression.</para>
    ///
    /// Uses a REAL <see cref="MessageBroker"/> with its databases isolated to temp files.
    /// </summary>
    public sealed class BrokerDuplicateNameRejectionTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public BrokerDuplicateNameRejectionTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_dupname_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_dupname_msg_{stamp}.db");
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
        public void Foreign_registration_cannot_claim_a_connected_terminals_name_or_steal_its_channel_port()
        {
            using var broker = new MessageBroker();

            // Alice is a live MT terminal: seeded launch nonce, channel port registered.
            broker.RegisterTerminal("Alice", docId: "DA", channelPort: 8801, nonce: "NA");

            // A foreign process claims the name "Alice" and offers its own port. No nonce.
            var result = broker.RegisterTerminal("Alice", docId: null, channelPort: 8802, nonce: null);

            // The claim is refused visibly, not silently renamed to "Alice-2".
            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));

            // Alice's row is untouched — crucially her ChannelPort was NOT repointed at the claimant,
            // which is what would have handed her pushed messages to it.
            var alice = Assert.Single(broker.GetTerminals(), t => t.Name == "Alice");
            Assert.Equal(8801, alice.ChannelPort);
            Assert.Equal("DA", alice.DocId);
        }

        [Fact]
        public void Channel_port_report_presenting_the_nonce_is_still_accepted()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Alice", docId: "DA", channelPort: 8801, nonce: "NA");

            // This is the channel server's own port report / drift heartbeat: same name, NO docId,
            // echoing the launch nonce it inherited from the terminal environment.
            var result = broker.RegisterTerminal("Alice", docId: null, channelPort: 8805, nonce: "NA");

            Assert.True(result.Success);

            // The port moved, so push delivery follows the terminal rather than being refused.
            var alice = Assert.Single(broker.GetTerminals(), t => t.Name == "Alice");
            Assert.Equal(8805, alice.ChannelPort);
        }

        [Fact]
        public void A_name_is_released_on_disconnect_rather_than_burned_forever()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Alice", docId: "DA", channelPort: 8801, nonce: "NA");
            var alice = Assert.Single(broker.GetTerminals(), t => t.Name == "Alice");
            broker.UnregisterTerminal(alice.Id);

            // A fresh terminal may take the now-free name. The gate only ever guards CONNECTED rows;
            // if it guarded disconnected ones too, every name would be permanently spent.
            var result = broker.RegisterTerminal("Alice", docId: "DB", channelPort: 8802, nonce: "NB");

            Assert.True(result.Success);
        }

        [Fact]
        public void Unassigned_stays_shareable_because_it_is_a_deliberate_sentinel()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Unassigned", docId: "DA", nonce: "NA");

            // Several anonymous terminals are allowed to share this name (see RegisterTerminalUnique,
            // which exempts it from suffixing for the same reason). The gate must not sweep it in.
            var result = broker.RegisterTerminal("Unassigned", docId: "DB", nonce: "NB");

            Assert.True(result.Success);
        }

        [Fact]
        public void A_row_that_never_had_a_nonce_fails_open_so_it_cannot_lock_its_own_owner_out()
        {
            using var broker = new MessageBroker();

            // A row registered without a seeded nonce — predates the gate, or arrived by a path that
            // does not seed one. Refusing it would lock out a terminal that has no nonce to present.
            broker.RegisterTerminal("Carol", docId: "DC", channelPort: 8803, nonce: null);

            var result = broker.RegisterTerminal("Carol", docId: null, channelPort: 8804, nonce: null);

            Assert.True(result.Success);
        }
    }
}
