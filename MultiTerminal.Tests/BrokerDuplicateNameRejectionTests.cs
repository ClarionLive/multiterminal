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
        public void An_adopted_row_with_no_nonce_is_still_protected_by_its_owning_pid()
        {
            using var broker = new MessageBroker();

            // An ADOPTED terminal: registered by name from a plain shell, so MT seeded no launch
            // nonce. All it has is the pid of the Claude Code process both of its children share.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: 4242);

            // A foreign process claims that name. Different parent, no nonce — nothing to prove with.
            var result = broker.RegisterTerminal("Lynn", docId: null, channelPort: 8811, nonce: null, ownerPid: 9999);

            Assert.False(result.Success);

            // Lynn keeps her port, so pushed messages keep reaching the session that claimed the name.
            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
            Assert.Equal(8810, lynn.ChannelPort);
        }

        [Fact]
        public void The_channel_server_of_an_adopted_session_re_registers_by_pid()
        {
            using var broker = new MessageBroker();

            // register_terminal arrives first, from the MCP server: name + owning pid, no port.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: 4242);

            // Then the sibling channel server reports its port under the SAME parent. It holds no
            // secret — the shared pid is the entire proof, which is what adoption rests on.
            var result = broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: 4242);

            Assert.True(result.Success);
            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
            Assert.Equal(8810, lynn.ChannelPort);
        }

        [Fact]
        public void A_channel_server_can_find_the_name_its_own_session_claimed()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: 4242);

            // The adoption lookup resolves on the pid the caller's PARENT owns, never on anything
            // the caller asserts about itself.
            Assert.Equal("Lynn", broker.GetTerminalNameByOwnerPid(4242));
            Assert.Null(broker.GetTerminalNameByOwnerPid(9999));
            Assert.Null(broker.GetTerminalNameByOwnerPid(0));
        }

        [Fact]
        public void An_established_pid_cannot_be_repointed_by_a_later_caller()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: 4242);
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8812, nonce: null, ownerPid: 4242);

            // set-if-empty: a caller may re-register with the right pid, but cannot move an
            // established row onto some other process. Otherwise a claimant could take a row and
            // then own it outright.
            Assert.Equal("Lynn", broker.GetTerminalNameByOwnerPid(4242));
            Assert.Null(broker.GetTerminalNameByOwnerPid(8888));
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
