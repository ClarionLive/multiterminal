using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
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

        /// <summary>
        /// Starts a real, short-lived child process and returns its pid, having waited for it to be
        /// bindable. Needed because a DEAD-owner row cannot be faked: a pid is bound only together
        /// with a verified start time, so an invented number is never bound at all and produces an
        /// UNOWNED row — a different state with different (and, as it turned out, opposite) handling.
        /// </summary>
        private static System.Diagnostics.Process StartBindableChild()
        {
            var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c pause",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });

            Assert.NotNull(child);

            // Kill the child if the readability assert fails — otherwise a failing run orphans a
            // `cmd.exe /c pause` that nothing will ever reap.
            try
            {
                Assert.True(MessageBroker.TryGetProcessStartTime(child.Id, out _),
                    "The child process's start time must be readable, or it cannot be bound as an owner.");
            }
            catch
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
                throw;
            }

            return child;
        }

        [Fact]
        public void The_new_owner_does_not_inherit_the_dead_sessions_port()
        {
            using var broker = new MessageBroker();

            // Session 1 must be a GENUINELY dead owner, which means a real process bound while alive
            // and then killed. An earlier version of this fact used an invented pid — which under the
            // verified-start-time rule is never bound at all, leaving the row UNOWNED rather than
            // Dead. It passed while pinning the opposite path, and the clearing bug it was supposed to
            // guard was in the Unowned branch it accidentally exercised.
            var child = StartBindableChild();
            try
            {
                broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: child.Id);

                var bound = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
                Assert.Equal(child.Id, bound.OwnerPid);          // the row really is OWNED...
                Assert.NotNull(bound.OwnerStartTime);            // ...by a verified incarnation

                child.Kill();

                // Assert the exit rather than discarding it. If the child outlived the wait, liveness
                // would read Alive, gate (4) would refuse the second registration, and this fact would
                // fail on the PORT assertion below — reporting the wrong cause. Fail on the real one.
                Assert.True(child.WaitForExit(10000),
                    "The child process did not exit within 10s, so the row's owner is not actually dead "
                    + "and this fact would otherwise fail for the wrong reason.");
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }

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

            // And the name genuinely changed hands.
            Assert.Equal(LivePid, lynn.OwnerPid);
        }

        [Fact]
        public void A_row_that_never_had_an_owner_keeps_its_port()
        {
            using var broker = new MessageBroker();

            // The negative of the fact above, and the regression that shipped: a row with a port but
            // NO bound owner has not changed hands, so its routing must survive.
            //
            // This state is ordinary, not exotic. A channel server reports its port with no ownerPid
            // (any session predating the plugin that sends one — i.e. every terminal already running
            // when this deploys), and spawned-agent and Oracle rows carry no pid at all. Clearing
            // here killed push delivery for a healthy terminal on its own next registration, and
            // because polling keeps working, nothing would have reported it.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: null);
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
            Assert.Equal(8810, lynn.ChannelPort);
        }

        /// <summary>
        /// A pid that genuinely CANNOT be bound right now: a real process, started and then reaped, so
        /// nothing can read its start time. Asserted rather than assumed — a pid that turned out to be
        /// readable would silently exercise the REBOUND branch, and the fact using it would pass while
        /// pinning the opposite path. Three facts on this ticket already failed exactly that way.
        /// </summary>
        private static int UnbindablePid()
        {
            var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c exit",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });

            Assert.NotNull(probe);
            int pid = probe.Id;
            try
            {
                Assert.True(probe.WaitForExit(10000),
                    "The probe process did not exit within 10s, so its pid is still bindable and this "
                    + "helper would hand back the wrong kind of pid.");
            }
            finally
            {
                try { if (!probe.HasExited) probe.Kill(); } catch { }
                probe.Dispose();
            }

            Assert.False(MessageBroker.TryGetProcessStartTime(pid, out _),
                "The reaped pid is still readable (pid reuse, or the probe outlived its wait). This "
                + "helper must return a pid that cannot be bound, or the caller silently tests the "
                + "rebound branch instead of the unbindable one.");

            return pid;
        }

        [Fact]
        public void A_dead_owners_port_is_cleared_even_when_the_new_owner_cannot_be_bound()
        {
            // Run 3b finding (a). `rebindSucceeded` used to gate the port clearing, so when the NEW
            // pid's start time was unreadable the row KEPT the dead session's port — and kept IsReady
            // true alongside it. The justification was that clearing would leave the row with "neither
            // owner nor delivery", but a dead session's port is not delivery: it is a number in the
            // recycled 8800-8899 range whose owning process is gone, and which may since have been
            // re-issued to a different live terminal. The old owner being Dead is what makes the port
            // stale, and that is independent of whether the new owner could be identified.
            using var broker = new MessageBroker();

            var child = StartBindableChild();
            int deadOwnerPid = child.Id;
            try
            {
                broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: deadOwnerPid);

                var bound = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
                Assert.Equal(deadOwnerPid, bound.OwnerPid);   // the row really is OWNED...
                Assert.NotNull(bound.OwnerStartTime);         // ...by a verified incarnation

                child.Kill();
                Assert.True(child.WaitForExit(10000),
                    "The child process did not exit within 10s, so the row's owner is not actually dead "
                    + "and this fact would otherwise fail for the wrong reason.");
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }

            // Session 2 claims the released name presenting a pid whose start time cannot be read.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: UnbindablePid());

            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");

            // PRECONDITION, asserted so this fact cannot go vacuous: the rebind must NOT have happened,
            // or we are on the already-covered rebound path and the finding is untested. The row still
            // carries the DEAD owner's pid because an unverifiable pid is deliberately never bound.
            Assert.Equal(deadOwnerPid, lynn.OwnerPid);

            // THE CLAIM: the stale routing goes anyway. Under the old conjunct both of these held the
            // dead session's values.
            Assert.Null(lynn.ChannelPort);
            Assert.False(lynn.IsReady);
        }

        [Fact]
        public void An_old_channel_servers_port_report_is_refused_and_its_push_delivery_stays_dead()
        {
            // Item 10, characterisation. VERSION SKEW: a channel server started before plugin commit
            // 7875686 does not echo the launch nonce (and predates ownerPid too). MT launched the
            // terminal, so the row it registers against DOES carry a nonce — which means the row is
            // held, the report proves nothing, and gate (4) refuses it.
            using var broker = new MessageBroker();

            // MT-launched terminal: the MCP server registers first, presenting the nonce it inherited.
            // reportPortToBroker deliberately sleeps 1s so this ordering is the normal one.
            broker.RegisterTerminal("Zoe", docId: "DZ", channelPort: null, nonce: "NONCE-Z", ownerPid: null);

            // The OLD channel server reports its port with neither nonce nor pid.
            var report = broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: null, ownerPid: null);

            Assert.False(report.Success);

            var zoe = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");

            // THE DEFECT: the terminal is connected and looks healthy, but has no route. Every push
            // is undeliverable, while get_messages polling keeps working — so nothing reports it.
            Assert.True(zoe.IsConnected);
            Assert.Null(zoe.ChannelPort);

            // ITEM 10 CYCLE 1: the FIRST refusal deliberately says nothing. At this instant Zoe is
            // indistinguishable from a healthy terminal whose own port report has not landed yet and
            // whose name a stranger just tried to claim — the live false positive this cycle fixes.
            Assert.Equal(0, zoe.ChannelPortRefusalCount);
            Assert.Null(zoe.LastChannelPortRefusalAt);

            // What distinguishes the real thing is that it COMES BACK. The drift gate only silences
            // the heartbeat once the roster shows its port, which for a refused server never happens,
            // so it re-reports the same port every ~30s forever. A stranger does not.
            var secondReport = broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: null, ownerPid: null);
            Assert.False(secondReport.Success);

            var zoeAfter = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");

            // THE FIX: corroborated, so now it says so. Without this the two lines above are the entire
            // observable state, and they are indistinguishable from a terminal that simply has not
            // reported a port yet.
            Assert.Equal(2, zoeAfter.ChannelPortRefusalCount);
            Assert.NotNull(zoeAfter.LastChannelPortRefusalAt);
            Assert.Null(zoeAfter.ChannelPort);
        }

        [Fact]
        public void A_single_claim_cannot_mark_a_terminal_that_has_not_reported_its_port_yet()
        {
            // ITEM 10, CYCLE 1 — THE FALSIFYING FACT. Run 4's guard ("count only when the incumbent
            // holds no route") passes every test in this file while still carrying this defect, which
            // is why it shipped: the guard is correct about the case it was written for and silent
            // about this one.
            //
            // MEASURED LIVE on 2026-09-09 against the deployed build, which is how it was found:
            //   10:03:42.150  Alice's row registered            (channelPort null)
            //   10:03:49.045  Alice's channel server accepted   (port set)
            // Seven seconds during which Alice was healthy, connected, and held no route. An adopted
            // session has the same window — 2.405s and 2.145s were measured the same afternoon.
            //
            // Any name claim landing in that window marked her dead, and PERMANENTLY: clearing the mark
            // requires an accepted port report, and her channel server's heartbeat is drift-gated, so
            // having already succeeded once it never sends another. list_terminals would then tell
            // every agent that a working terminal needed restarting, for MT's entire uptime.
            using var broker = new MessageBroker();

            // A terminal mid-startup: registered, healthy, port report not yet sent. This is the exact
            // state of EVERY terminal for the first seconds of its life.
            broker.RegisterTerminal("Zoe", docId: "DZ", channelPort: null, nonce: "NONCE-Z", ownerPid: null);

            var starting = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.True(starting.IsConnected);
            Assert.Null(starting.ChannelPort);

            // A stranger claims the name. registerPortOnce sends name AND port together, so a claim
            // carrying a port needs no impostor — a second shell registering as "Zoe" produces exactly
            // this call, which is how it happens in practice.
            var claim = broker.RegisterTerminal("Zoe", docId: null, channelPort: 8899, nonce: null, ownerPid: null);
            Assert.False(claim.Success);   // refused, correctly — that half was never in doubt

            // THE CLAIM: one refusal proves nothing about Zoe. It is somebody else's failure, and Zoe
            // has not yet had the chance to succeed. She must be unmarked.
            var afterClaim = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(0, afterClaim.ChannelPortRefusalCount);
            Assert.Null(afterClaim.LastChannelPortRefusalAt);

            // And the fix must not be "delete the feature". Zoe's own report now arrives and is
            // accepted, so she ends healthy with a live route and a clean record — the ordinary
            // startup this whole fact is protecting.
            var own = broker.RegisterTerminal("Zoe", docId: null, channelPort: 8820, nonce: "NONCE-Z", ownerPid: null);
            Assert.True(own.Success);

            var healthy = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(8820, healthy.ChannelPort);
            Assert.Equal(0, healthy.ChannelPortRefusalCount);
        }

        [Fact]
        public void Recovery_clears_the_corroboration_history_not_just_the_count()
        {
            // ITEM 10, CYCLE 1. A partial tally that survives a successful report is a half-armed trap:
            // the row's count is reset, so nothing looks wrong, but one refusal is still banked. The
            // next unrelated one-shot claim then pairs with it and produces a verdict that neither
            // event on its own justified — the original false positive, reintroduced through the back
            // door and harder to see because it needs two unrelated causes.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Zoe", docId: "DZ", channelPort: null, nonce: "NONCE-Z", ownerPid: null);

            // One refusal banked (below the threshold, so nothing is written to the row).
            broker.RegisterTerminal("Zoe", docId: null, channelPort: 8899, nonce: null, ownerPid: null);
            Assert.Equal(0, Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe").ChannelPortRefusalCount);

            // Zoe's own report lands. Delivery demonstrably works.
            Assert.True(broker.RegisterTerminal("Zoe", docId: null, channelPort: 8820, nonce: "NONCE-Z", ownerPid: null).Success);

            // Her server restarts, so the row's port is cleared by the owner-change path and she is once
            // again mid-startup with no route. A stranger claims the name once.
            broker.RegisterTerminal("Zoe2", docId: "DZ2", channelPort: null, nonce: "NONCE-Z2", ownerPid: null);
            var zoe = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            zoe.ChannelPort = null;   // what a channel-server exit leaves behind

            broker.RegisterTerminal("Zoe", docId: null, channelPort: 8899, nonce: null, ownerPid: null);

            // THE CLAIM: that is the FIRST refusal of the new run, not the second of the old one.
            // If the accepted report had cleared only the verdict and not the evidence, this would
            // read 2 and Zoe would be declared dead on the strength of two strangers months apart.
            var after = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(0, after.ChannelPortRefusalCount);
            Assert.Null(after.LastChannelPortRefusalAt);
        }

        [Fact]
        public void A_name_claim_refusal_is_not_counted_as_a_dead_channel()
        {
            // The distinction the whole signal rests on. A refusal WITHOUT a port is a name claim being
            // rejected — gate (4) working exactly as designed, and the observed CHECK 4 behaviour where
            // a stranger is told it cannot have a name in use. It says nothing about anyone's delivery.
            // Counting it would make the health field fire on healthy refusals, and a signal that fires
            // when nothing is wrong is worse than no signal: it trains its reader to ignore it.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Zoe", docId: "DZ", channelPort: null, nonce: "NONCE-Z", ownerPid: null);

            var claim = broker.RegisterTerminal("Zoe", docId: null, channelPort: null, nonce: null, ownerPid: null);

            Assert.False(claim.Success);   // refused, correctly

            var afterClaim = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(0, afterClaim.ChannelPortRefusalCount);
            Assert.Null(afterClaim.LastChannelPortRefusalAt);

            // Asserting only the zero above would be a fact that passes when counting is BROKEN
            // ENTIRELY — "not counted" and "nothing is ever counted" are indistinguishable from one
            // side. (Confirmed: with the counter reverted, the zero-only version stayed green while
            // three sibling facts went red.) So drive the OTHER side in the same fact: a port report,
            // refused for the identical reason by the identical branch, MUST count. What is being
            // pinned is the discrimination, not either value on its own.
            // ITEM 10 CYCLE 1: it takes a corroborated pair to count now, so drive the same port twice.
            // That is not a weakening of this fact — the discrimination it pins is unchanged, because
            // the name claim above never counts NO MATTER HOW OFTEN it is repeated (it carries no port),
            // while port reports do once corroborated. The two sides still differ, and still for the
            // reason this fact exists to state.
            Assert.False(broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: null, ownerPid: null).Success);
            var report = broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: null, ownerPid: null);

            Assert.False(report.Success);   // same gate, same refusal...

            var afterReport = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(2, afterReport.ChannelPortRefusalCount);   // ...but this one is a dead channel
            Assert.NotNull(afterReport.LastChannelPortRefusalAt);

            // The other half of the discrimination, made explicit: repeating the PORTLESS name claim
            // cannot corroborate anything, because corroboration is keyed on (name, port) and a claim
            // carrying no port never enters the ledger at all. Without this the fact above would be
            // consistent with "any two refusals count", which is the defect one cycle removed.
            broker.RegisterTerminal("Zoe", docId: null, channelPort: null, nonce: null, ownerPid: null);
            broker.RegisterTerminal("Zoe", docId: null, channelPort: null, nonce: null, ownerPid: null);

            var afterMoreClaims = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(2, afterMoreClaims.ChannelPortRefusalCount);   // unchanged by the claims
        }

        [Fact]
        public void A_refused_report_from_someone_else_does_not_mark_a_healthy_terminal_dead()
        {
            // PIPELINE RUN 4, found independently by four gates. The first cut counted every refusal
            // that carried a port, reasoning that "carries a port" means "is a port report". It does
            // not distinguish WHOSE report was refused — and a refusal is by definition the case where
            // the broker could NOT attribute the report to this row. So the count landed on the
            // incumbent, who may be entirely healthy.
            //
            // No impostor is needed for this: registerPortOnce sends name AND port together, so a
            // second shell claiming a live name produces exactly this call.
            using var broker = new MessageBroker();

            // A healthy terminal with a working route.
            broker.RegisterTerminal("Zoe", docId: "DZ", channelPort: null, nonce: "NONCE-Z", ownerPid: null);
            broker.RegisterTerminal("Zoe", docId: null, channelPort: 8820, nonce: "NONCE-Z", ownerPid: null);

            var healthy = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(8820, healthy.ChannelPort);

            // Somebody else reports a DIFFERENT port for the same name and is refused — correctly.
            var intruder = broker.RegisterTerminal("Zoe", docId: null, channelPort: 8899, nonce: null, ownerPid: null);
            Assert.False(intruder.Success);

            // THE CLAIM: that refusal was about the caller, not about Zoe. Zoe still holds a live route,
            // so nothing about her delivery has been demonstrated and she must not be marked dead.
            // Before the fix this read 1, and list_terminals told every agent Zoe needed restarting —
            // permanently, because the reset needs an accepted port report and a healthy terminal's
            // drift-gated heartbeat never sends another one.
            var afterIntruder = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(8820, afterIntruder.ChannelPort);   // route untouched
            Assert.Equal(0, afterIntruder.ChannelPortRefusalCount);
            Assert.Null(afterIntruder.LastChannelPortRefusalAt);
        }

        [Fact]
        public void A_recovered_terminal_stops_reporting_a_dead_channel()
        {
            // A health signal that cannot go back to healthy is just a second way to be wrong. The row
            // survives a channel-server restart through the name-match path, so a cumulative count would
            // leave a terminal that RECOVERED reading as broken forever.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Zoe", docId: "DZ", channelPort: null, nonce: "NONCE-Z", ownerPid: null);
            broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: null, ownerPid: null);
            broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: null, ownerPid: null);

            var sick = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(2, sick.ChannelPortRefusalCount);

            // The terminal is restarted; its channel server now echoes the nonce, so the report is
            // admitted and delivery is live again.
            broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: "NONCE-Z", ownerPid: null);

            var healed = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Equal(8815, healed.ChannelPort);
            Assert.Equal(0, healed.ChannelPortRefusalCount);
            Assert.Null(healed.LastChannelPortRefusalAt);
        }

        [Fact]
        public void The_proposed_same_port_migration_path_cannot_apply_because_there_is_no_port_to_match()
        {
            // Item 10 proposed two fixes. This pins why the SECOND one — "allow an old no-nonce port
            // refresh only when it MATCHES the row's already-recorded port, so it cannot repoint
            // delivery" — does not address the skew it was written for.
            //
            // It presumes the row already holds the port being re-reported. It never does: the very
            // first report is refused, so ChannelPort is still null, and every subsequent 30s
            // heartbeat compares against that null. A match rule over a value that was never allowed
            // to be written can only ever refuse. The idea is sound for a REFRESH and this is not a
            // refresh — it is an initial registration that never succeeded.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Zoe", docId: "DZ", channelPort: null, nonce: "NONCE-Z", ownerPid: null);

            for (int heartbeat = 0; heartbeat < 3; heartbeat++)
            {
                broker.RegisterTerminal("Zoe", docId: null, channelPort: 8815, nonce: null, ownerPid: null);
            }

            var zoe = Assert.Single(broker.GetTerminals(), t => t.Name == "Zoe");
            Assert.Null(zoe.ChannelPort);   // still nothing to match against, after three heartbeats

            // And every one of those heartbeats is now counted, which is what distinguishes an ONGOING
            // failure from a single blip — the thing a reader actually needs in order to act.
            Assert.Equal(3, zoe.ChannelPortRefusalCount);
        }

        [Fact]
        public void A_live_owners_heartbeat_never_reaches_the_port_clearing_branch_at_all()
        {
            using var broker = new MessageBroker();

            // Renamed from A_same_pid_re_registration_keeps_its_port, which claimed to test the
            // "owner actually changed" guard and could not: a LIVE owner makes rowIsUnowned false, so
            // execution never reaches the guarded block. It was green under the buggy guard and the
            // fixed one alike — vacuous as a guard test.
            //
            // What it genuinely pins is worth keeping under an honest name: the channel server
            // re-registers under the same pid on its 30s drift heartbeat, and that path must leave a
            // healthy terminal's routing alone. It is protected by the OUTER condition (a live owner
            // is not unowned), not by the inner one.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);
            broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null, ownerPid: LivePid);

            var lynn = Assert.Single(broker.GetTerminals(), t => t.Name == "Lynn");
            Assert.Equal(8810, lynn.ChannelPort);

            // The row is still owned by the live session throughout — the heartbeat never re-binds.
            Assert.Equal(LivePid, lynn.OwnerPid);
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

        /// <summary>
        /// PIPELINE RUN 5 — the gate was a check-then-act with no mutual exclusion.
        ///
        /// <para>Found independently by three gates (Codex security, Codex cross-model adversary, and
        /// the Claude debugger) and by none of the 885 tests that preceded them, because every existing
        /// fact tells a SINGLE-THREADED story: call register, then assert. The defect needs two callers
        /// inside one window.</para>
        ///
        /// <para><c>RegisterTerminal</c> scanned <c>_terminals</c> for a connected row with this name,
        /// then — hundreds of lines and two <c>Process.GetProcessById</c> syscalls later — minted a row
        /// keyed on a fresh <see cref="Guid"/>. The mint cannot collide, so <c>TryAdd</c> provided no
        /// mutual exclusion on the NAME. Both racers read "nobody holds it", both skipped the gate, and
        /// both inserted. Delivery resolves by <c>FirstOrDefault</c>-over-name, so messages then went to
        /// an arbitrary one of the two while every call reported success.</para>
        ///
        /// <para>FALSIFICATION — PERFORMED, not asserted (2026-09-08). The <c>lock (_registrationLock)</c>
        /// was removed from <c>RegisterTerminal</c> and this fact was re-run against that build. It
        /// failed with <b>"Expected exactly one connected row named 'Lynn'; found 16"</b> — every one of
        /// the sixteen racers minted its own row, so the gate was not merely leaky under contention, it
        /// was fully open. The lock was then restored and the fact passes. That is what makes this test
        /// evidence rather than decoration.</para>
        ///
        /// <para>The 16/16 result is also the answer to "was the window really reachable?" — it is not a
        /// narrow interleaving. The scan and the mint are separated by hundreds of lines including two
        /// process-probe syscalls, so under any real contention the racers all land inside it.</para>
        /// </summary>
        [Fact]
        public void Concurrent_registrations_of_one_free_name_produce_exactly_one_connected_row()
        {
            using var broker = new MessageBroker();

            const int racers = 16;
            using var start = new System.Threading.Barrier(racers);
            var results = new System.Collections.Concurrent.ConcurrentBag<bool>();

            var threads = new System.Threading.Thread[racers];
            for (int i = 0; i < racers; i++)
            {
                threads[i] = new System.Threading.Thread(() =>
                {
                    // Release every thread at the same instant so they contend for the real window
                    // rather than arriving in a comfortable sequence.
                    start.SignalAndWait();
                    var r = broker.RegisterTerminal("Lynn", docId: null, channelPort: null, nonce: null);
                    results.Add(r.Success);
                });
                threads[i].IsBackground = true;
            }

            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join(TimeSpan.FromSeconds(30));

            // THE INVARIANT. "Fail-closed duplicate rejection" has to mean one row, or the name is not
            // an identity at all — and channel delivery keys on nothing but the name.
            var lynnRows = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.Where(broker.GetTerminals(),
                    t => string.Equals(t.Name, "Lynn", StringComparison.OrdinalIgnoreCase) && t.IsConnected));

            Assert.True(lynnRows.Count == 1,
                $"Expected exactly one connected row named 'Lynn'; found {lynnRows.Count}. " +
                "Two rows means the duplicate-name gate was evaluated against state that moved underneath it.");

            // Every racer should have been told something true: the winner minted, the rest reused that
            // same row. Nobody should be holding a success that refers to a row which no longer decides
            // delivery.
            Assert.Equal(racers, results.Count);
        }

        /// <summary>
        /// PIPELINE RUN 6, debugger HIGH — the lock enrolled half the writers.
        ///
        /// <para>4c3f60d added <c>_registrationLock</c> to fix a check-then-act on
        /// <c>IsConnected</c>/<c>ChannelPort</c>, and enrolled only the REGISTRATION writers.
        /// <c>DisconnectTerminalByName</c> mutates the same two fields, so the race stayed wide open
        /// from the other side — through the same window the 16-racer fact above documents, which is
        /// several hundred lines and two process-probe syscalls long.</para>
        ///
        /// <para>This pairing is not exotic. It happens at EVERY SESSION END: the SessionEnd hook POSTs
        /// /api/messaging/disconnect while the channel server's 30s drift heartbeat POSTs
        /// /api/messaging/register, on independent thread-pool threads.</para>
        ///
        /// <para>The end state that must not occur is a row reading CONNECTED while holding the port of
        /// a channel server that has exited. Ports 8800-8899 are recycled, so that number may already
        /// belong to a different live terminal whose channel server does not check the envelope's
        /// <c>to</c> field — one agent's messages delivered into another's session, with nothing
        /// reporting it. Clearing the port is the single line that prevents that, and the race was
        /// overwriting exactly it.</para>
        /// </summary>
        [Fact]
        public void A_disconnect_is_never_overwritten_by_a_concurrent_registration()
        {
            const int rounds = 40;

            for (int round = 0; round < rounds; round++)
            {
                using var broker = new MessageBroker();

                // A live MT-launched terminal holding a route.
                broker.RegisterTerminal("Lynn", docId: "DL", channelPort: 8840, nonce: "NL");

                // Its channel server's drift heartbeat and its SessionEnd disconnect, released together.
                using var start = new Barrier(2);
                Exception failure = null;

                var register = new Thread(() =>
                {
                    try
                    {
                        start.SignalAndWait();
                        broker.RegisterTerminal("Lynn", docId: null, channelPort: 8842, nonce: "NL");
                    }
                    catch (Exception ex) { failure = ex; }
                });

                var disconnect = new Thread(() =>
                {
                    try
                    {
                        start.SignalAndWait();
                        broker.DisconnectTerminalByName("Lynn");
                    }
                    catch (Exception ex) { failure = ex; }
                });

                register.IsBackground = true;
                disconnect.IsBackground = true;
                register.Start();
                disconnect.Start();
                register.Join(TimeSpan.FromSeconds(30));
                disconnect.Join(TimeSpan.FromSeconds(30));

                Assert.Null(failure);

                // THE INVARIANT. Either order is legitimate — the disconnect may land first and the
                // registration then legitimately revive the row, or the registration may land first and
                // the disconnect then tear it down. What must NEVER happen is the interleaving: the
                // disconnect's field writes surviving as "disconnected" while the registration's port
                // write survives on the same row. A row is connected WITH a port, or disconnected WITHOUT
                // one; the mixture is the corruption.
                // ⚠️ OBSERVABILITY LIMIT, stated rather than glossed. GetTerminals() filters on
                // IsConnected, so a DISCONNECTED row cannot be inspected from outside the broker at
                // all. Of the two torn orderings only ONE is visible here:
                //
                //   visible   — disconnect's ChannelPort=null lands last while register's
                //               IsConnected=true survives  => CONNECTED row holding NO port, even
                //               though the registration that connected it supplied 8842.
                //   invisible — register's ChannelPort=8842 lands last while disconnect's
                //               IsConnected=false survives  => disconnected row holding a live port.
                //
                // So this fact is a STRESS CHECK that can only ever catch real corruption (it has no
                // false-positive mode), not a complete proof. The complete, deterministic guarantee is
                // the source census below it — which is why that one exists and why this one is not
                // load-bearing on its own.
                var lynnRows = broker.GetTerminals().Where(t => t.Name == "Lynn").ToList();

                if (lynnRows.Count == 1)
                {
                    Assert.True(lynnRows[0].ChannelPort != null,
                        $"Round {round}: the row is CONNECTED but holds no port. Both writers that set "
                        + "IsConnected=true also set a port, so this state is only reachable by the "
                        + "disconnect's ChannelPort=null landing between the registration's two field "
                        + "writes — a torn write across the pair the lock exists to keep together.");
                }
            }
        }

        /// <summary>
        /// PIPELINE RUN 6, debugger HIGH — the deterministic half of the disconnect-race guarantee.
        ///
        /// <para>The barrier fact above can only observe one of the two torn orderings, and only if the
        /// interleaving happens to occur. That makes it a stress check, not a proof: a race test that
        /// passes tells you nothing, because passing is also what an un-fixed build does most of the
        /// time. This census is the part that actually holds the line.</para>
        ///
        /// <para>It asserts the STRUCTURAL property directly — that every broker method which mutates
        /// <c>IsConnected</c> or <c>ChannelPort</c> outside the registration decision does so under
        /// <c>_registrationLock</c>. That is exactly what 4c3f60d got wrong: it introduced the lock as
        /// the fix for a check-then-act on these two fields, then enrolled only the registration
        /// writers, leaving the disconnect side racing the very window the lock was added to close.</para>
        /// </summary>
        [Fact]
        public void Every_disconnect_writer_mutates_connection_state_under_the_registration_lock()
        {
            string brokerPath = LocateRepoFile(Path.Combine("MCPServer", "Services", "MessageBroker.cs"));
            string[] lines = File.ReadAllLines(brokerPath);

            // The two methods that tear down a terminal's connection state from outside the
            // registration path. Both mutate IsConnected; DisconnectTerminalByName also nulls the port.
            string[] guarded = { "public void UnregisterTerminal(", "public bool DisconnectTerminalByName(" };

            foreach (string signature in guarded)
            {
                int start = Array.FindIndex(lines, l => l.Contains(signature, StringComparison.Ordinal));
                Assert.True(start >= 0, $"Could not find {signature} in MessageBroker.cs — this census has gone stale and is no longer checking anything.");

                // Scan the method body: from its signature forward, bounded so a rename elsewhere
                // cannot silently widen the search until it stumbles onto some other method's lock.
                //
                // ⚠️ COMMENT LINES ARE STRIPPED FIRST, and that is not tidiness. On the first run this
                // census failed against the FIXED code, because the explanatory comment above
                // DisconnectTerminalByName narrates the bug — it contains the literal
                // "IsConnected = false" describing step 3 of the losing interleaving, which sorted
                // before the real `lock` line and made the correct code look unguarded. A string census
                // that reads prose as if it were code is the same defect this run already found twice
                // (the `?.` blind spot, and a falsifier that shared it). Strip the prose.
                int end = Math.Min(start + 80, lines.Length);
                string body = string.Join("\n", lines[start..end]
                    .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

                Assert.True(body.Contains("IsConnected = false", StringComparison.Ordinal),
                    $"{signature} no longer contains 'IsConnected = false'. If the teardown moved, move this census with it.");

                Assert.True(MutationIsInsideRegistrationLock(body, "IsConnected = false"),
                    $"{signature} mutates connection state OUTSIDE the _registrationLock block.\n"
                    + "That is the Run 6 defect: 4c3f60d added the lock to fix a check-then-act on\n"
                    + "IsConnected/ChannelPort and enrolled only the registration writers, so a SessionEnd\n"
                    + "disconnect could be overwritten by a concurrent port-report registration — leaving a\n"
                    + "row marked connected while holding the port of a channel server that had exited.\n"
                    + "Ports 8800-8899 are recycled, so that port may already belong to a different live\n"
                    + "terminal whose channel server does not check the envelope's `to` field.");
            }

            // ⚠️ THE PORT CLEAR IS CHECKED SEPARATELY, and that is not redundancy. The race corrupts the
            // PAIR — a row is connected WITH a route or disconnected WITHOUT one, and the mixture is the
            // corruption. An earlier version of this census checked only IsConnected, so deleting the
            // `ChannelPort = null` line entirely — the single line whose own comment says it exists to
            // stop delivery to a dead channel server — would not have been noticed.
            int disconnectStart = Array.FindIndex(lines, l => l.Contains("public bool DisconnectTerminalByName(", StringComparison.Ordinal));
            string disconnectBody = string.Join("\n", lines[disconnectStart..Math.Min(disconnectStart + 80, lines.Length)]
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.True(disconnectBody.Contains("ChannelPort = null", StringComparison.Ordinal),
                "DisconnectTerminalByName no longer clears ChannelPort. That line is what stops messages "
                + "being delivered to a port whose channel server has exited and which the 8800-8899 "
                + "allocator may already have reissued to a different live terminal.");

            Assert.True(MutationIsInsideRegistrationLock(disconnectBody, "ChannelPort = null"),
                "DisconnectTerminalByName clears ChannelPort OUTSIDE the _registrationLock block. It must "
                + "be written atomically with IsConnected — the whole defect is a torn write across that pair.");
        }

        /// <summary>
        /// Whether <paramref name="mutation"/> occurs inside the lexical body of the FIRST
        /// <c>lock (_registrationLock)</c> block in <paramref name="body"/>.
        ///
        /// <para>⚠️ THIS EXISTS BECAUSE THE OBVIOUS VERSION WAS WRONG, and wrong in the way that matters.
        /// The first cut asserted <c>indexOf(lock) &lt; indexOf(mutation)</c> — textual ORDER, not
        /// CONTAINMENT. The Run 6 debugger falsified it empirically by replaying the logic against a copy
        /// with the mutation moved after the lock's closing brace and getting
        /// <c>lockAt=116 &lt; mutAt=567 =&gt; PASS</c>. So the census certified precisely the regression it
        /// was written to catch: lock the lookup, mutate after release. It was described in its own commit
        /// as "the deterministic guarantee" while being unable to see that.</para>
        ///
        /// <para>Assumption, stated because it is load-bearing: braces inside interpolated strings
        /// (<c>{name}</c>) are balanced, so depth tracking is unaffected by them. An unbalanced brace
        /// inside a string literal would break this, and none exists in the scanned methods.</para>
        /// </summary>
        private static bool MutationIsInsideRegistrationLock(string body, string mutation)
        {
            int lockAt = body.IndexOf("lock (_registrationLock)", StringComparison.Ordinal);
            if (lockAt < 0) return false;

            int open = body.IndexOf('{', lockAt);
            if (open < 0) return false;

            int depth = 0;
            int close = -1;
            for (int i = open; i < body.Length; i++)
            {
                if (body[i] == '{') depth++;
                else if (body[i] == '}')
                {
                    depth--;
                    if (depth == 0) { close = i; break; }
                }
            }

            if (close < 0) return false;   // unbalanced — refuse rather than guess

            int mutationAt = body.IndexOf(mutation, StringComparison.Ordinal);
            return mutationAt > open && mutationAt < close;
        }

        /// <summary>
        /// Proves <see cref="MutationIsInsideRegistrationLock"/> can actually fail — the guard the
        /// previous version of this census did not have, and whose absence let it certify the regression
        /// it existed to catch.
        /// </summary>
        [Fact]
        public void The_lock_containment_rule_rejects_a_mutation_after_the_closing_brace()
        {
            // The regression shape: the lock wraps only the LOOKUP, and the field write happens after
            // release. Textually the lock still precedes the mutation, which is exactly why the
            // first-occurrence comparison passed it.
            string lockedLookupOnly =
                "TerminalInfo terminal;\n"
                + "lock (_registrationLock)\n"
                + "{\n"
                + "    terminal = _terminals.Values.FirstOrDefault(t => t.IsConnected);\n"
                + "}\n"
                + "if (terminal != null)\n"
                + "{\n"
                + "    terminal.IsConnected = false;\n"
                + "}\n";

            Assert.False(MutationIsInsideRegistrationLock(lockedLookupOnly, "IsConnected = false"),
                "A mutation AFTER the lock's closing brace must be rejected. The first-occurrence "
                + "comparison this replaced returned PASS here, which is how a census described as the "
                + "deterministic guarantee came to be unable to see its own regression.");

            // The correct shape must still be accepted, or the rule is just always-false — which would
            // pass this fact while breaking the census for every legitimate implementation.
            string properlyGuarded =
                "TerminalInfo terminal;\n"
                + "lock (_registrationLock)\n"
                + "{\n"
                + "    terminal = _terminals.Values.FirstOrDefault(t => t.IsConnected);\n"
                + "    if (terminal != null)\n"
                + "    {\n"
                + "        terminal.IsConnected = false;\n"
                + "    }\n"
                + "}\n";

            Assert.True(MutationIsInsideRegistrationLock(properlyGuarded, "IsConnected = false"),
                "A mutation nested inside the lock block — including inside a nested if — must be accepted.");

            // And a body with no lock at all is rejected rather than throwing.
            Assert.False(MutationIsInsideRegistrationLock("terminal.IsConnected = false;\n", "IsConnected = false"));
        }

        /// <summary>
        /// PIPELINE RUN 6 delta, cross-model adversary MEDIUM — two long names must not corroborate
        /// each other.
        ///
        /// <para>The ledger key clamped the name to a bounded prefix. Two distinct connected names
        /// sharing that prefix and reporting the same refused port therefore shared ONE record, so
        /// terminal A's first refusal could serve as terminal B's corroborating second — marking B dead
        /// off a single one-shot refusal. That is the exact false positive the corroboration rule was
        /// added to prevent, manufactured by the bounding that was supposed to be a safety measure.</para>
        ///
        /// <para>Names come from an unauthenticated local endpoint, so this is a shape a caller can
        /// choose rather than an unlucky accident.</para>
        /// </summary>
        [Fact]
        public void Two_long_names_sharing_a_prefix_do_not_corroborate_each_others_refusals()
        {
            using var broker = new MessageBroker();

            // Identical for far longer than any key clamp, differing only at the very end.
            string shared = new string('x', 200);
            string nameA = shared + "-alpha";
            string nameB = shared + "-beta";

            broker.RegisterTerminal(nameA, docId: "DA", channelPort: null, nonce: "NA");
            broker.RegisterTerminal(nameB, docId: "DB", channelPort: null, nonce: "NB");

            // ONE refused port report against each — a single one-shot refusal per terminal, which must
            // never be enough to mark either of them.
            Assert.False(broker.RegisterTerminal(nameA, docId: null, channelPort: 8815, nonce: null).Success);
            Assert.False(broker.RegisterTerminal(nameB, docId: null, channelPort: 8815, nonce: null).Success);

            var rowA = Assert.Single(broker.GetTerminals(), t => t.Name == nameA);
            var rowB = Assert.Single(broker.GetTerminals(), t => t.Name == nameB);

            Assert.True(rowA.ChannelPortRefusalCount == 0 && rowB.ChannelPortRefusalCount == 0,
                $"Neither terminal had a second refusal of its own, so neither may be marked. "
                + $"Got A={rowA.ChannelPortRefusalCount}, B={rowB.ChannelPortRefusalCount}. A non-zero "
                + "value here means the two names collapsed onto one ledger record and one terminal's "
                + "refusal corroborated the other's — a dead-channel verdict neither event justified.");
        }

        /// <summary>
        /// PIPELINE RUN 6, cross-model adversary HIGH — the guard fired essentially never.
        ///
        /// <para><c>GetTerminalNameByOwnerPid</c> resolves an unbound channel server's identity from its
        /// parent pid. It documented itself as returning null "rather than guess if more than one
        /// non-temporary row remains", but the code returned null ONLY when two rows tied on
        /// <c>OwnerBoundAt</c> to the tick — a measure-zero event — and otherwise handed back the
        /// newest. So the stated rule and the implemented rule had diverged, and the guess was the
        /// ordinary path.</para>
        ///
        /// <para>Two real rows under one owner pid is not contrived: a rename leaves the old row still
        /// connected, and subagents share the parent's MCP server process. Delivery is keyed on NAME,
        /// so a wrong answer does not degrade — it binds a channel server to somebody else's identity
        /// and silently delivers their messages to it.</para>
        /// </summary>
        [Fact]
        public void Two_real_rows_under_one_owner_pid_resolve_to_nothing_rather_than_a_guess()
        {
            using var broker = new MessageBroker();

            // Two REAL names (neither the "Unassigned" sentinel, neither a temporary "Agent *" row),
            // both connected, both owned by the same live process — and deliberately bound at DIFFERENT
            // times, which is the case the old tie-only guard let through.
            broker.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);
            Thread.Sleep(15);
            broker.RegisterTerminal("Morgan", docId: null, channelPort: 8811, nonce: null, ownerPid: LivePid);

            var rows = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(
                broker.GetTerminals(), t => t.IsConnected && t.OwnerPid == LivePid));
            Assert.Equal(2, rows.Count);   // the ambiguous topology genuinely exists

            // THE CLAIM: ambiguous means ambiguous. Refusing costs an unbound caller one retry;
            // guessing costs a misrouted conversation that nothing reports.
            string resolved = broker.GetTerminalNameByOwnerPid(LivePid);

            Assert.True(resolved == null,
                $"Expected null for an ambiguous owner pid; got '{resolved}'. Returning a name here "
                + "binds a channel server to whichever row happened to sort first, and message delivery "
                + "is keyed on name — so the wrong answer is not a degraded answer, it is another "
                + "terminal's messages delivered to this one.");

            // And the fix must not be "always return null", which would break adoption entirely.
            // One unambiguous row still resolves.
            using var single = new MessageBroker();
            single.RegisterTerminal("Lynn", docId: null, channelPort: 8810, nonce: null, ownerPid: LivePid);
            Assert.Equal("Lynn", single.GetTerminalNameByOwnerPid(LivePid));
        }

        /// <summary>
        /// PIPELINE RUN 5 — a refusal that nobody reads is the same as no gate.
        ///
        /// <para>Three MainForm launch sites called <c>RegisterTerminal</c> and discarded the
        /// <c>RegisterResult</c> entirely. Gate (4) made registration fallible for real names, so a
        /// refusal left no broker row bound to the document while execution continued into
        /// <c>StartTerminal</c> with <c>MULTITERMINAL_NAME</c> set to the refused name: a correctly
        /// titled tab that no message can reach, with nothing logged.</para>
        ///
        /// <para>This is a source census rather than a behavioural test, matching the repo's standing
        /// answer to a contract no compiler checks (see <c>ChannelFlagContractTests</c>). It is the
        /// shape that fits: the defect is "a call site forgot to look at a return value", which has no
        /// runtime signature short of driving WinForms.</para>
        ///
        /// <para>The three sites that legitimately discard the result register <c>"Unassigned"</c>,
        /// which gate (4) exempts by an explicit conjunct, so a refusal is impossible for them. The
        /// census therefore keys on the REAL-NAME calls only.</para>
        ///
        /// <para>FALSIFICATION — PERFORMED, not asserted (2026-09-08). One of the three fixed sites was
        /// reverted to its bare <c>_mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId, ...)</c>
        /// form and this fact was re-run. It failed, naming <c>MainForm.cs:3813</c> exactly. The site was
        /// then restored and the fact passes. A census that has never been shown to fail is a census
        /// that might be matching nothing.</para>
        /// </summary>
        [Fact]
        public void No_MainForm_launch_site_registers_a_real_name_without_reading_the_result()
        {
            string mainFormPath = LocateRepoFile("MainForm.cs");
            string[] lines = File.ReadAllLines(mainFormPath);

            var offenders = new System.Collections.Generic.List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                // ⚠️ NORMALISE THE NULL-CONDITIONAL RECEIVER BEFORE MATCHING (Run 6 verifier finding).
                // The census used to filter on the literal "Broker.RegisterTerminal", so every
                // `Broker?.RegisterTerminal` site was invisible to it — including MainForm.cs:6619,
                // which registers the REAL name "Oracle" and discards the result. A census whose name
                // asserts "no site does X" while being structurally unable to see a whole call SHAPE
                // is worse than no census: it reports a safety it never checked, and it had already
                // been "falsified" once against a non-`?.` fixture, which proved only that it could
                // see the shape it was already looking at.
                if (!IsRegisterTerminalCall(trimmed)) continue;

                // A call whose result is read starts with an assignment ("var result =", "terminalName =").
                // A discarded one begins directly with the receiver.
                if (RegisterResultIsRead(trimmed)) continue;

                // "Unassigned" is a deliberate shared sentinel and is exempted inside gate (4), so it
                // can never be refused — discarding its result is safe.
                if (trimmed.Contains("\"Unassigned\"", StringComparison.Ordinal)) continue;

                // The name being registered is a variable; look back a few lines for the sentinel
                // assignment that identifies these as placeholder registrations.
                bool sentinelNearby = false;
                for (int back = Math.Max(0, i - 4); back < i; back++)
                {
                    if (lines[back].Contains("= \"Unassigned\"", StringComparison.Ordinal)) sentinelNearby = true;
                }
                if (sentinelNearby) continue;

                offenders.Add($"MainForm.cs:{i + 1}: {trimmed}");
            }

            Assert.True(offenders.Count == 0,
                "These MainForm sites register a REAL name and discard the RegisterResult, so a gate-(4) "
                + "refusal would launch a terminal under a name the broker rejected:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// Proves the census above can actually fail: a discarded real-name call is recognised as an
        /// offender by the same rule. Without this, a census that silently matched nothing would pass
        /// forever and report safety it never checked.
        /// </summary>
        [Fact]
        public void The_MainForm_census_rule_actually_flags_a_discarded_real_name_call()
        {
            string discarded = "_mcpServer.Broker.RegisterTerminal(terminalName, doc.DocId, isTeamLead, nonce: doc.LaunchNonce);";
            string read = "terminalName = PreRegisterTerminalWithName(doc.DocId, terminalName, isTeamLead);";
            string sentinel = "_mcpServer.Broker.RegisterTerminal(\"Unassigned\", doc.DocId);";

            // ⚠️ RUN 6. Every fixture above uses a bare `.` receiver, so this fact proved only that the
            // rule could see the shape it was already looking at. The real MainForm offender wrote
            // `Broker?.RegisterTerminal` and was invisible to both the rule AND to this falsifier —
            // a blind spot cannot be found by fixtures that share it. These two carry the `?.` form.
            string discardedNullConditional = "_mcpServer?.Broker?.RegisterTerminal(OracleService.OracleName, _oracleService.DocId);";
            string readNullConditional = "var regResult = _mcpServer?.Broker?.RegisterTerminal(agentName, agentDocId);";

            Assert.False(ResultIsRead(discarded), "A bare call must be recognised as discarding its result.");
            Assert.True(ResultIsRead(read) || !read.Contains("Broker.RegisterTerminal", StringComparison.Ordinal),
                "An assigned call must not be flagged.");
            Assert.True(sentinel.Contains("\"Unassigned\"", StringComparison.Ordinal),
                "The sentinel exemption must key on the literal the gate exempts.");

            Assert.True(Matches(discardedNullConditional),
                "A null-conditional receiver must still be RECOGNISED as a RegisterTerminal call — this is "
                + "the exact miss that let MainForm.cs:6619 hide from the census for a whole run.");
            Assert.False(ResultIsRead(discardedNullConditional),
                "A discarded `?.` call must be flagged, not silently skipped.");
            Assert.True(ResultIsRead(readNullConditional),
                "An assigned `?.` call must NOT be flagged — widening the matcher must not turn every "
                + "null-conditional site into a false offender. Note this is the half that breaks if the "
                + "IndexOf is left matching the un-normalised literal: it returns -1, which is less than "
                + "the '=' index, so the call reads as discarded and the census cries wolf on a safe site.");

            static bool Matches(string trimmed) => IsRegisterTerminalCall(trimmed);

            static bool ResultIsRead(string trimmed) => RegisterResultIsRead(trimmed);
        }

        // ⚠️ THE CENSUS AND ITS FALSIFIER MUST SHARE ONE IMPLEMENTATION, not two copies of the same
        // idea. Found while falsifying the Run 6 fix: reverting ONLY the census's normalisation made
        // the census go red (correctly, naming two safe `?.` sites it now mis-read as discarding) while
        // the falsifier stayed GREEN, because the falsifier carried its own private copy of the rule.
        // A falsifier that cannot fail when the thing it certifies breaks is decoration. These two
        // helpers are the single definition both now go through, so the drift is not possible.

        /// <summary>
        /// Whether a source line calls <c>Broker.RegisterTerminal</c>, INCLUDING through a
        /// null-conditional receiver (<c>Broker?.RegisterTerminal</c>). Normalising <c>?.</c> to
        /// <c>.</c> is the whole point: matching the bare literal is what hid MainForm.cs:6619 for a
        /// full run.
        /// </summary>
        private static bool IsRegisterTerminalCall(string trimmed) =>
            Canonicalise(trimmed).Contains("Broker.RegisterTerminal", StringComparison.Ordinal);

        /// <summary>
        /// Whether the call's result is assigned rather than discarded. Both the '=' search and the
        /// method-name search run on the CANONICAL form — an <c>IndexOf</c> against the un-normalised
        /// literal returns -1 on a <c>?.</c> line, which is less than any '=' index, so every safe
        /// null-conditional site would read as discarded and the census would cry wolf.
        /// </summary>
        private static bool RegisterResultIsRead(string trimmed)
        {
            string canonical = Canonicalise(trimmed);
            return canonical.Contains("=", StringComparison.Ordinal)
                   && canonical.IndexOf('=') < canonical.IndexOf("Broker.RegisterTerminal", StringComparison.Ordinal);
        }

        /// <summary>Collapses null-conditional receivers so one literal matches both call shapes.</summary>
        private static string Canonicalise(string trimmed) =>
            trimmed.Replace("?.", ".", StringComparison.Ordinal);

        /// <summary>Walks up from the test binary to the repo root to find a source file.</summary>
        private static string LocateRepoFile(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, fileName);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                $"Could not locate {fileName} by walking up from {AppContext.BaseDirectory}. "
                + "This census test needs the source tree, not just the build output.");
        }
    }
}
