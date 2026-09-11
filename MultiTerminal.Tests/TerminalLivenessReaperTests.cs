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
            // Unowned is ordinary: spawned agents, Oracle and gateway terminals carry no pid, and a
            // pid probe is meaningless for them. Alive is the everyday case: a sweep over healthy
            // terminals must be a pure no-op.
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
    }
}
