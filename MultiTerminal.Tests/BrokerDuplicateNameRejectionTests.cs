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
        /// <summary>
        /// A pid that genuinely names a running process — this test process itself.
        /// <para>These facts used to use an invented number (4242). After pipeline Run 1 the gate
        /// requires the owning process to still be ALIVE, and to be the same incarnation the pid was
        /// bound to (<see cref="MultiTerminal.MCPServer.Models.TerminalInfo.OwnerStartTime"/>), because
        /// a bare pid is not an identity: Windows recycles pids, and an adopted row is never marked
        /// disconnected, so a stale row could otherwise hold a name for MT's whole uptime and let a
        /// recycled pid inherit the claim.</para>
        /// <para>An invented pid therefore no longer proves anything — it reads as a corpse, the row
        /// counts as unheld, and the gate correctly fails OPEN. Using the real process id keeps these
        /// facts exercising the live mechanism rather than a path that happens to agree with them.</para>
        /// </summary>
        private static int LivePid => Environment.ProcessId;

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
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);

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
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            // Then the sibling channel server reports its port under the SAME parent. It holds no
            // secret — the shared pid is the entire proof, which is what adoption rests on.
            var result = broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);

            Assert.True(result.Success);
            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
            Assert.Equal(8810, lynn.ChannelPort);
        }

        [Fact]
        public void A_channel_server_can_find_the_name_its_own_session_claimed()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            // The adoption lookup resolves on the pid the caller's PARENT owns, never on anything
            // the caller asserts about itself.
            Assert.Equal("Lynn", broker.GetTerminalNameByOwnerPid(LivePid));
            Assert.Null(broker.GetTerminalNameByOwnerPid(9999));
            Assert.Null(broker.GetTerminalNameByOwnerPid(0));
        }

        [Fact]
        public void An_established_pid_cannot_be_repointed_by_a_later_caller()
        {
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8812, nonce: null, ownerPid: LivePid);

            // set-if-empty: a caller may re-register with the right pid, but cannot move an
            // established row onto some other process. Otherwise a claimant could take a row and
            // then own it outright.
            Assert.Equal("Lynn", broker.GetTerminalNameByOwnerPid(LivePid));
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

        // ---------------------------------------------------------------------------------------
        // Pipeline Run 1 regressions. Each of the three below is a defect that shipped and was
        // caught in review, not a hypothetical. They are the reason the pid means less than it did.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_pid_does_not_defeat_the_nonce_on_a_row_that_has_one()
        {
            using var broker = new MessageBroker();

            // An MT-launched terminal: held by a real 32-char secret.
            broker.RegisterTerminal("Alice", docId: "DA", channelPort: 8801, nonce: "NONCE-A", ownerPid: LivePid);

            // A caller that learned the owning pid — by sweeping channel-identity, say — presents it
            // instead of the nonce. The first cut OR'd the two proofs and stamped a pid on EVERY row,
            // so this SUCCEEDED and repointed Alice's channel port. The pid is discovery, not
            // authorization: a row holding a nonce is held by that nonce alone.
            var result = broker.RegisterTerminal("Alice", docId: null, channelPort: 8899, nonce: null, ownerPid: LivePid);

            Assert.False(result.Success);

            var alice = Assert.Single(broker.GetTerminals(), t => t.Name == "Alice");
            Assert.Equal(8801, alice.ChannelPort);
        }

        [Fact]
        public void A_dead_owner_releases_the_name_instead_of_burning_it()
        {
            using var broker = new MessageBroker();

            // An adopted row whose owning process is NOT running. This is the ordinary end state of
            // every adopted session: it has no docId for UnregisterTerminal and no MULTITERMINAL_NAME
            // for the SessionEnd hook, and no reaper exists — so the row stays IsConnected forever.
            // Treating that corpse as a live holder is what made gate (4) refuse the name's real owner
            // on their very next shell, which is how the feature would have failed on first use.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: 424242);

            // The same person opens a new shell: different pid, still no nonce, AND NO PORT.
            //
            // Omitting the port matters — an earlier version of this fact passed one, which is the one
            // thing the real caller never does. mcp/index.js deliberately omits channelPort because
            // the channel server reports its own, so supplying it here masked the bug where the row
            // kept the DEAD session's port (see The_new_owner_does_not_inherit_the_dead_sessions_port).
            var result = broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            Assert.True(result.Success);

            // ...and the row must now BELONG to the new session, not merely have admitted it.
            //
            // Asserting only Success is what let a half-fix through review: the name was released but
            // the pid was never rebound (the set-if-empty guard saw a non-null corpse pid), so the row
            // still pointed at a dead process. Registration reported success while
            // GetTerminalNameByOwnerPid returned null for the live pid — and an adopted session has no
            // MULTITERMINAL_NAME, so its channel server had no other way to learn its name and never
            // bound. Push delivery was dead with every visible signal saying healthy: exactly the
            // observability trap this ticket exists to close.
            Assert.Equal("Lynn", broker.GetTerminalNameByOwnerPid(LivePid));

            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
            Assert.Equal(LivePid, lynn.OwnerPid);
            Assert.NotNull(lynn.OwnerStartTime);

            // And protection is RESTORED for the new owner, not spent — a stranger is refused again.
            var stranger = broker.RegisterTerminal("Lynn", docId: null, channelPort: 8899, nonce: null, ownerPid: 424243);
            Assert.False(stranger.Success);
        }

        [Fact]
        public void The_new_owner_does_not_inherit_the_dead_sessions_port()
        {
            using var broker = new MessageBroker();

            // Session 1 registered a port; its process then died without ever being disconnected
            // (an adopted row has no docId for UnregisterTerminal, and the SessionEnd hook does not
            // run without MULTITERMINAL_NAME).
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: 424242);

            // Session 2 claims the released name. The real caller sends NO port — the channel server
            // reports its own once it starts listening.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");

            // The port must NOT survive the ownership change. Ports come from a small recycled range
            // (8800-8899) handed out to whatever is free, so 8810 may already belong to a DIFFERENT
            // live terminal's channel server — which does not enforce the envelope's `to` field. The
            // POST would return 200, delivery would be marked done, and Lynn's messages would land in
            // someone else's session with nothing reporting it. DisconnectTerminalByName nulls the
            // port for exactly this reason; an ownership change is its adopted-row analogue.
            Assert.Null(lynn.ChannelPort);

            // The old session's handshake does not carry over either.
            Assert.False(lynn.IsReady);
        }

        [Fact]
        public void A_same_pid_re_registration_keeps_its_port()
        {
            using var broker = new MessageBroker();

            // The guard above must be conditional on the owner ACTUALLY changing. The channel server
            // re-registers under the same pid on its drift heartbeat; clearing the port there would
            // kill push delivery for every healthy adopted terminal on every heartbeat.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
            Assert.Equal(8810, lynn.ChannelPort);
        }

        [Fact]
        public void A_released_name_cannot_be_captured_permanently_by_an_attacker_nonce()
        {
            using var broker = new MessageBroker();

            // An adopted session held "Lynn" by pid, then exited. The name is released.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: 424242);

            // A hostile local process claims the released name and supplies a nonce of its own
            // choosing. It may take the name — an unheld name is claimable, by design — but seeding
            // that nonce would make the claim PERMANENT: a nonce is never liveness-checked and never
            // expires, and pidProves is disabled for nonce-bearing rows, so the real owner could
            // never reclaim the name for the rest of MT's uptime. That is the same permanent-lockout
            // class the liveness change exists to remove, re-entered through the door beside it.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8899, nonce: "ATTACKER-NONCE", ownerPid: 424243);

            // The legitimate owner comes back and must still be able to claim its name by pid.
            var legitimate = broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            Assert.True(legitimate.Success);
            Assert.Equal("Lynn", broker.GetTerminalNameByOwnerPid(LivePid));
        }

        [Fact]
        public void The_pid_lookup_never_hands_back_the_shared_placeholder()
        {
            using var broker = new MessageBroker();

            // "Unassigned" is the deliberate shared sentinel, never anyone's identity. Returning it
            // would let a channel server bind the placeholder and start answering for it.
            broker.RegisterTerminal("Unassigned", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            Assert.Null(broker.GetTerminalNameByOwnerPid(LivePid));
        }

        [Fact]
        public void A_recycled_pid_cannot_inherit_a_dead_sessions_claim()
        {
            using var broker = new MessageBroker();

            // A row bound to a pid that is not live. An unrelated process later holding that same pid
            // number must NOT be able to adopt the name — pid plus start time names one incarnation,
            // and the lookup refuses to answer for a pid whose process is gone.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: 424242);

            Assert.Null(broker.GetTerminalNameByOwnerPid(424242));
        }

        [Fact]
        public void The_pid_lookup_refuses_to_guess_between_two_real_identities()
        {
            using var broker = new MessageBroker();

            // One Claude process can own several connected rows — a subagent shares its parent's MCP
            // server, and a rename leaves the first row connected. FirstOrDefault over
            // ConcurrentDictionary.Values picked one at random, so an unbound channel server could
            // bind the WRONG name and start answering another agent's messages.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);
            broker.RegisterTerminal("Robin", docId: null, channelPort: 8811, nonce: null, ownerPid: LivePid);

            // Whatever it returns, it must be one of the two real names and must be STABLE — never a
            // coin flip between calls.
            string first = broker.GetTerminalNameByOwnerPid(LivePid);
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(first, broker.GetTerminalNameByOwnerPid(LivePid));
            }

            Assert.True(first == null || first == "Lynn" || first == "Robin",
                $"Lookup returned '{first}', which is neither of the registered names nor a refusal.");
        }
    }
}
