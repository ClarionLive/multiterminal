using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Liveness reaper tests (task d1151661 item 3).
    ///
    /// <para>Found in Owner testing, 2026-09-10: a force-killed terminal (Diana) left the Terminals
    /// list on the first read, because item 0 hides a Dead owner, but her card stayed on the Attention
    /// rail. The rail, Office, Tasks panel, Dashboard header and Chat react to
    /// <see cref="MessageBroker.TerminalDisconnected"/>, which only an explicit teardown raises, and
    /// nothing raised it for a session that died. The reaper turns "provably dead" into that
    /// event.</para>
    ///
    /// <para>As in <see cref="TerminalRosterLivenessTests"/>, the negative facts are the load-bearing
    /// ones. Reaping is a WRITE: getting Unknown wrong no longer just hides a live terminal, it
    /// disconnects it, nulls its port and marks its profile offline.</para>
    ///
    /// Uses a REAL <see cref="MessageBroker"/> with its databases isolated to temp files, and REAL
    /// processes killed to manufacture genuine corpses.
    /// </summary>
    public sealed class TerminalLivenessReaperTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public TerminalLivenessReaperTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_reaper_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_reaper_msg_{stamp}.db");
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
        /// Registers <paramref name="name"/> owned by a real child process, then kills the child, so
        /// the row's owner is genuinely Dead rather than simulated.
        /// </summary>
        private static TerminalInfo RegisterWithDeadOwner(MessageBroker broker, string name, string docId = null, string nonce = null)
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

            try
            {
                Assert.True(MessageBroker.TryGetProcessStartTime(child.Id, out _),
                    "The child's start time must be readable, or it cannot be bound as an owner.");

                broker.RegisterTerminal(name, docId: docId, channelPort: 8801, nonce: nonce, ownerPid: child.Id);

                // PRECONDITION: really owned by a verified incarnation, or the kill proves nothing.
                var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == name);
                Assert.Equal(child.Id, row.OwnerPid);
                Assert.NotNull(row.OwnerStartTime);

                child.Kill();
                Assert.True(child.WaitForExit(10000),
                    "The child did not exit within 10s, so the owner is not actually dead.");
                return row;
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }
        }

        [Fact]
        public void A_dead_owner_is_disconnected_and_announced_exactly_once()
        {
            using var broker = new MessageBroker();
            var disconnected = new List<string>();
            broker.TerminalDisconnected += (_, t) => disconnected.Add(t.Name);

            var row = RegisterWithDeadOwner(broker, "Diana");

            var released = broker.ReapDeadOwnerTerminals();

            // THE CLAIM: this is what the Attention rail was waiting for.
            Assert.Equal(new[] { "Diana" }, released);
            Assert.Equal(new[] { "Diana" }, disconnected);

            // A real teardown, matching what /quit's DisconnectTerminalByName leaves behind: not
            // connected, and no route to a channel server that has exited.
            Assert.False(row.IsConnected);
            Assert.Null(row.ChannelPort);
            Assert.DoesNotContain(broker.GetAllConnectedTerminals(), t => t.Name == "Diana");

            var profile = broker.GetProfile("Diana");
            Assert.True(profile.Success, "precondition: registration created a profile to mark offline");
            Assert.False(profile.Profile.IsOnline,
                "The STORED flag must be cleared too. GetTerminals filters on it, and a stale true is "
                + "what made 'online' disagree with itself before item 2.");

            // Idempotent: the next sweep finds nothing and announces nothing. Without this the rail
            // would receive a disconnect every 30s for every corpse MT has ever seen.
            Assert.Empty(broker.ReapDeadOwnerTerminals());
            Assert.Single(disconnected);
        }

        [Fact]
        public void An_owner_that_cannot_be_probed_is_NEVER_reaped()
        {
            // ⚠️ THE LOAD-BEARING FACT. Weaken the eligibility rule in FindDeadOwnerCandidates from
            // `!= Dead` to `== Alive` and this goes red; the dead-owner fact above passes either way.
            // Unknown is a LIVE terminal we could not read (MT unelevated while claude is elevated).
            // Reaping it disconnects someone who is sitting there working, and the 30s channel
            // heartbeat would revive it only for the next sweep to kill it again.
            //
            // The shape used is the documented pid-without-start-time Unknown. The access-denied
            // Unknown needs a process this test cannot read, which is machine-dependent (elevation,
            // CI runner). Both shapes reach the reaper through the same ResolveOwnerLiveness verdict,
            // and there is deliberately no earlier guard that could decide this row first.
            using var broker = new MessageBroker();
            var disconnected = new List<string>();
            broker.TerminalDisconnected += (_, t) => disconnected.Add(t.Name);

            broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null,
                                    ownerPid: Environment.ProcessId);
            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
            Assert.NotNull(row.OwnerStartTime);
            row.OwnerStartTime = null;   // the documented Unknown shape: a pid with no start time

            // Anti-vacuity: the write landed on broker state, not a copy.
            Assert.Null(Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin").OwnerStartTime);

            Assert.Empty(broker.ReapDeadOwnerTerminals());
            Assert.True(row.IsConnected);
            Assert.Equal(8801, row.ChannelPort);
            Assert.Empty(disconnected);
        }

        [Fact]
        public void Unowned_and_alive_owners_are_never_reaped()
        {
            // Unowned is ordinary: spawned agents, Unassigned placeholders and gateway terminals carry
            // no pid, and a pid probe is meaningless for them. Oracle is NOT in that set: its channel
            // server reports a pid, so a sweep during its auto-restart gap disconnects it, the same
            // as its SessionEnd hook would. That cost is accepted. Alive is the everyday case: a
            // sweep over healthy terminals must be a pure no-op.
            using var broker = new MessageBroker();
            var disconnected = new List<string>();
            broker.TerminalDisconnected += (_, t) => disconnected.Add(t.Name);

            broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null, ownerPid: null);
            broker.RegisterTerminal("Wren", docId: null, channelPort: 8802, nonce: null, ownerPid: Environment.ProcessId);

            Assert.Null(Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin").OwnerPid);
            Assert.NotNull(Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Wren").OwnerStartTime);

            Assert.Empty(broker.ReapDeadOwnerTerminals());
            Assert.Equal(2, broker.GetAllConnectedTerminals().Count);
            Assert.Empty(disconnected);
        }

        [Fact]
        public void A_row_rebound_to_a_live_owner_after_it_was_judged_dead_is_NOT_reaped()
        {
            // ⚠️ THE COLLISION THE TWO PHASES EXIST FOR. The sweep judges Diana dead; before it tears
            // her down, the Owner relaunches Diana in the same tab. A name-match registration REUSES
            // the row object and rebinds its owner, so the row reference is unchanged, and a reap that
            // trusted it would disconnect the brand-new live session. The re-verify inside the lock
            // (same pid AND same start time) is the only thing that tells the two apart.
            //
            // Driven deterministically through the two phases rather than by racing threads: a race
            // test that passes proves nothing, since passing is also what the unfixed build does most
            // of the time.
            using var broker = new MessageBroker();
            var disconnected = new List<string>();
            broker.TerminalDisconnected += (_, t) => disconnected.Add(t.Name);

            var row = RegisterWithDeadOwner(broker, "Diana");

            var candidate = Assert.Single(broker.FindDeadOwnerCandidates());
            Assert.Same(row, candidate.Terminal);

            // The relaunch lands between the phases. The dead owner releases the name to its new
            // live owner, which is gate (4)'s own rule for a pid-only row.
            var relaunch = broker.RegisterTerminal("Diana", docId: null, channelPort: 8803, nonce: null,
                                                   ownerPid: Environment.ProcessId);
            Assert.True(relaunch.Success, "precondition: a dead pid-only owner must release its name");

            var live = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Diana");
            Assert.Equal(Environment.ProcessId, live.OwnerPid);   // precondition: the rebind happened

            Assert.False(broker.TryReapDeadOwner(candidate),
                "The reaper tore down a row whose owner had been rebound to a LIVE process. That is "
                + "the relaunched session being disconnected moments after it registered.");
            Assert.True(live.IsConnected);
            Assert.Equal(8803, live.ChannelPort);
            Assert.Empty(disconnected);
        }

        [Fact]
        public void A_launched_terminal_reaped_after_death_frees_its_name_as_quit_would()
        {
            // The intended change in gate (4)'s behaviour, stated so nobody mistakes it for an
            // accident. A nonce-bearing row holds its name after death while it is still CONNECTED
            // (TerminalRosterLivenessTests.Hidden_from_the_listing_does_NOT_mean_the_name_is_available).
            // Gate (4) only considers connected rows, so a name is released on disconnect. Reaping
            // makes a killed terminal release its name exactly as a /quit does. Before it, a killed
            // tab's name stayed burned for MT's whole uptime.
            using var broker = new MessageBroker();

            RegisterWithDeadOwner(broker, "Diana", docId: "D1", nonce: "N1");
            Assert.False(broker.RegisterTerminal("Diana", docId: "D2", channelPort: 8802, nonce: "N2").Success,
                "precondition: before the reap, the dead nonce row still holds the name");

            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());

            Assert.True(broker.RegisterTerminal("Diana", docId: "D2", channelPort: 8802, nonce: "N2").Success);
        }

        [Fact]
        public void Closing_a_reaped_terminals_tab_does_not_sign_out_the_live_agent_that_took_its_name()
        {
            // ⚠️ PIPELINE RUN 3 BLOCKING (debugger). Freeing a dead terminal's name while its tab is
            // still open made this sequence routine:
            //   1. Diana's claude dies; her tab (docId D1) stays open; the reaper frees "Diana".
            //   2. The Owner launches Diana in another tab (D2), which creates a second, live row.
            //   3. The Owner closes the old tab, which calls UnregisterTerminal("D1").
            // UnregisterTerminal found the corpse by docId, disconnected-or-not, and repeated its
            // name-keyed teardown. The event made MainForm evict EVERY "Diana" Attention card, and
            // SetProfileOffline("Diana") hid the live row from GetTerminals.
            using var broker = new MessageBroker();

            RegisterWithDeadOwner(broker, "Diana", docId: "D1", nonce: "N1");
            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());

            var relaunch = broker.RegisterTerminal("Diana", docId: "D2", channelPort: 8802, nonce: "N2",
                                                   ownerPid: Environment.ProcessId);
            Assert.True(relaunch.Success, "precondition: the reaped name must be claimable by the relaunch");
            Assert.Contains(broker.GetTerminals(), t => t.Name == "Diana" && t.DocId == "D2");

            var disconnected = new List<string>();
            broker.TerminalDisconnected += (_, t) => disconnected.Add($"{t.Name}/{t.DocId}");

            broker.UnregisterTerminal("D1");   // the old tab closes

            Assert.Empty(disconnected);
            Assert.Contains(broker.GetTerminals(), t => t.Name == "Diana" && t.DocId == "D2");
            Assert.True(broker.GetProfile("Diana").Profile.IsOnline,
                "Closing the dead tab marked the live Diana's profile offline, which hides her from "
                + "GetTerminals, the roster and every panel.");
        }

        [Fact]
        public void Closing_a_tab_disconnects_its_live_session_not_an_earlier_corpse_under_the_same_docId()
        {
            // Same tab, relaunched after a reap: one docId now owns a disconnected row AND a live one,
            // because rows are never removed. UnregisterTerminal must tear down the live one.
            //
            // Stated honestly: before the fix this picked a row in dictionary order, so the old code
            // could pass here by luck. The deterministic guard is the ordering; this fact pins the
            // outcome.
            using var broker = new MessageBroker();

            var corpse = RegisterWithDeadOwner(broker, "Diana", docId: "D1", nonce: "N1");
            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());

            broker.RegisterTerminal("Diana", docId: "D1", channelPort: 8803, nonce: "N1", ownerPid: Environment.ProcessId);
            var live = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Diana");
            Assert.NotSame(corpse, live);   // precondition: two rows share D1

            var disconnected = new List<TerminalInfo>();
            broker.TerminalDisconnected += (_, t) => disconnected.Add(t);

            broker.UnregisterTerminal("D1");

            Assert.False(live.IsConnected, "the tab closed but its live session was left connected");
            Assert.Same(live, Assert.Single(disconnected));
        }

        [Fact]
        public void Reaping_one_of_two_same_name_rows_leaves_the_name_live_for_name_keyed_effects()
        {
            // ⚠️ PIPELINE RUN 3, adversary M1. The event is per ROW; the Attention eviction and the
            // profile write are per NAME. Two connected rows can carry one name: an Unassigned tab
            // renames to a name whose dead row has not been swept yet. Reaping the dead one must not
            // sign out the live one. MainForm's Attention handler asks
            // IsAgentNameHeldByLiveTerminal DURING the raise, so that is what this checks.
            using var broker = new MessageBroker();

            var dead = RegisterWithDeadOwner(broker, "Diana", docId: "D1", nonce: "N1");
            broker.RegisterTerminal("Unassigned", docId: "D2", channelPort: 8804, nonce: "N2");
            broker.RegisterTerminal("Diana", docId: "D2", channelPort: 8804, nonce: "N2", ownerPid: Environment.ProcessId);

            var rows = broker.GetAllConnectedTerminals().Where(t => t.Name == "Diana").ToList();
            Assert.True(rows.Count == 2,
                $"precondition: the rename path must produce two connected 'Diana' rows (got {rows.Count}); "
                + "without them this fact exercises nothing");
            var live = Assert.Single(rows, t => !ReferenceEquals(t, dead));

            bool? nameLiveDuringRaise = null;
            broker.TerminalDisconnected += (_, t) => nameLiveDuringRaise = broker.IsAgentNameHeldByLiveTerminal(t.Name);

            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());

            Assert.True(nameLiveDuringRaise == true,
                "During the reaper's TerminalDisconnected the name read as NOT live, so MainForm would "
                + "evict the live Diana's Attention cards.");
            Assert.True(live.IsConnected);
            Assert.True(broker.GetProfile("Diana").Profile.IsOnline, "the live same-name row's profile was marked offline");
            Assert.Contains(broker.GetTerminals(), t => ReferenceEquals(t, live));
        }

        [Fact]
        public void Closing_one_of_two_same_name_tabs_keeps_the_other_signed_in()
        {
            // The same name-keyed rule on the tab-close path, for a row that is still CONNECTED, so the
            // already-disconnected short-circuit does not apply. Only IsAgentNameHeldByLiveTerminal
            // stands between this close and the other Diana's profile.
            using var broker = new MessageBroker();

            RegisterWithDeadOwner(broker, "Diana", docId: "D1", nonce: "N1");   // not yet swept
            broker.RegisterTerminal("Unassigned", docId: "D2", channelPort: 8805, nonce: "N2");
            broker.RegisterTerminal("Diana", docId: "D2", channelPort: 8805, nonce: "N2", ownerPid: Environment.ProcessId);
            Assert.Equal(2, broker.GetAllConnectedTerminals().Count(t => t.Name == "Diana"));   // precondition

            var disconnected = new List<string>();
            broker.TerminalDisconnected += (_, t) => disconnected.Add(t.DocId);

            broker.UnregisterTerminal("D1");

            Assert.Equal(new[] { "D1" }, disconnected);   // the row-keyed event still fires (Office)
            Assert.True(broker.GetProfile("Diana").Profile.IsOnline,
                "Closing the dead Diana's tab marked the name offline while the other Diana is live.");
            Assert.Contains(broker.GetTerminals(), t => t.Name == "Diana" && t.DocId == "D2");
        }

        [Fact]
        public void A_name_held_only_by_a_corpse_is_not_live_but_one_held_by_an_unprobeable_owner_is()
        {
            // The other half of IsAgentNameHeldByLiveTerminal. Every fact above needs it to say "live"
            // when a live row exists; none needed it to say "not live" when only a corpse is left. So
            // a helper counting Dead rows as live passed all of them (falsified, Run 3 fix round).
            // That would keep a dead name online on the roster, since the offline write never runs.
            using var broker = new MessageBroker();

            RegisterWithDeadOwner(broker, "Diana", docId: "D1", nonce: "N1");   // connected, not yet swept
            Assert.Contains(broker.GetAllConnectedTerminals(), t => t.Name == "Diana");   // precondition

            Assert.False(broker.IsAgentNameHeldByLiveTerminal("Diana"),
                "A name whose only connected row has a dead owner was reported live.");

            // ...and the three-state rule holds here too: Unknown and Unowned are live.
            broker.RegisterTerminal("Robin", docId: null, channelPort: 8806, nonce: null, ownerPid: null);
            Assert.True(broker.IsAgentNameHeldByLiveTerminal("robin"));   // Unowned, case-insensitive

            broker.RegisterTerminal("Wren", docId: null, channelPort: 8807, nonce: null, ownerPid: Environment.ProcessId);
            Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Wren").OwnerStartTime = null;   // Unknown
            Assert.True(broker.IsAgentNameHeldByLiveTerminal("Wren"));

            Assert.False(broker.IsAgentNameHeldByLiveTerminal(null));
            Assert.False(broker.IsAgentNameHeldByLiveTerminal("Nobody"));
        }

        [Fact]
        public void A_live_session_whose_pid_cannot_be_bound_is_unowned_not_dead()
        {
            // ⚠️ PIPELINE RUN 3, adversary M2. A session admitted by its nonce onto a dead-owner row,
            // presenting a pid whose start time cannot be read (e.g. claude elevated, MT not). The
            // rebind is correctly refused, but the DEAD owner's identity stayed on the row. So a live
            // session read as Dead: hidden by the roster, and disconnected by every sweep.
            using var broker = new MessageBroker();

            var row = RegisterWithDeadOwner(broker, "Lynn", docId: "D1", nonce: "N1");

            var readmit = broker.RegisterTerminal("Lynn", docId: "D1", channelPort: 8811, nonce: "N1",
                                                  ownerPid: BrokerDuplicateNameRejectionTests.UnbindablePid());
            Assert.True(readmit.Success, "precondition: the nonce must admit the session");
            Assert.Same(row, Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Lynn"));

            Assert.Null(row.OwnerPid);
            Assert.Null(row.OwnerStartTime);
            Assert.Empty(broker.ReapDeadOwnerTerminals());
            Assert.True(row.IsConnected);
            Assert.Contains(broker.GetTerminals(), t => ReferenceEquals(t, row));
        }

        [Fact]
        public void The_attention_eviction_asks_whether_the_name_is_still_live_before_evicting()
        {
            // The M1 fact above proves the broker answers correctly during the raise. It cannot prove
            // MainForm ASKS: that handler is a WinForms member no unit test can construct. Item 2's
            // known gap was exactly that shape (a correct helper nobody is proven to call), so pin the
            // call site structurally.
            string mainForm = File.ReadAllText(BrokerDuplicateNameRejectionTests.LocateRepoFile("MainForm.cs"));

            int handler = mainForm.IndexOf("private void OnMcpTerminalDisconnectedForAttention(", StringComparison.Ordinal);
            Assert.True(handler >= 0, "OnMcpTerminalDisconnectedForAttention not found; this census has gone stale.");

            int next = mainForm.IndexOf("\n        private ", handler + 1, StringComparison.Ordinal);
            string body = mainForm.Substring(handler, (next < 0 ? mainForm.Length : next) - handler);
            string code = string.Join("\n", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            int ask = code.IndexOf("IsAgentNameHeldByLiveTerminal(", StringComparison.Ordinal);
            int evict = code.IndexOf("NoteTerminalGone(", StringComparison.Ordinal);
            Assert.True(evict >= 0, "the handler no longer evicts; move this census with the eviction");
            Assert.True(ask >= 0 && ask < evict,
                "The Attention handler evicts by name without first asking IsAgentNameHeldByLiveTerminal, so a "
                + "dead or closed row deletes the cards of a live terminal that carries the same name.");
        }

        [Fact]
        public void The_timer_shell_survives_a_failing_sweep_and_reports_what_it_released()
        {
            var log = new List<string>();
            int calls = 0;

            using var reaper = new TerminalLivenessReaper(() =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("database is locked");
                return new[] { "Diana" };
            }, log.Add);

            reaper.Sweep();   // must not throw: it runs on a timer thread
            reaper.Sweep();

            Assert.Equal(2, calls);
            Assert.Contains(log, l => l.Contains("sweep failed", StringComparison.Ordinal) && l.Contains("database is locked", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("Diana", StringComparison.Ordinal));

            reaper.Dispose();
            reaper.Sweep();   // a tick already in flight at dispose must not reach the broker
            Assert.Equal(2, calls);
        }

        [Fact]
        public void A_throwing_logger_cannot_escape_the_sweep()
        {
            // Run 3 code review: during shutdown the logger can be a disposed DebugLogService. Sweep's
            // catch logs, so an unguarded Log threw from inside the catch and escaped onto the timer
            // thread, which ends the process.
            using var reaper = new TerminalLivenessReaper(
                () => throw new InvalidOperationException("sweep failed"),
                _ => throw new ObjectDisposedException("DebugLogService"));

            reaper.Sweep();   // THE CLAIM: returns normally
        }
    }
}
