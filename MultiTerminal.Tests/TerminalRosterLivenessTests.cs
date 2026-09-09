using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Roster liveness tests (task d1151661).
    ///
    /// <para>The Owner's report: <i>"when I do /quit, Robin is still in the Terminals list even though
    /// his terminal is gone."</i> Reproduced immediately — three dead sessions sitting at
    /// <c>IsConnected=true</c>, two of them still advertising a port their process no longer owned.</para>
    ///
    /// <para>Nothing releases those rows. An adopted session has no docId for
    /// <c>UnregisterTerminal</c> and no <c>MULTITERMINAL_NAME</c> for the SessionEnd hook's disconnect
    /// POST (<c>session-status-hook.js</c> early-returns on it), the channel server has no exit handler
    /// at all, and there is no reaper. So <see cref="MessageBroker.GetTerminals"/> — the UI-listing
    /// view — now asks the OS instead of trusting the dying process to report in.</para>
    ///
    /// <para><b>The second fact is the load-bearing one.</b> <c>ResolveOwnerLiveness</c> has FOUR
    /// states, and its own remarks record why (Run 2 correction): collapsing "cannot verify" into
    /// "gone" made a <c>Win32Exception</c> — MT unelevated while claude is elevated, a protected
    /// process, a cross-session pid — read a LIVE owner as a corpse. A filter written as
    /// <c>!= Alive</c> instead of <c>== Dead</c> passes the first fact here and erases live terminals
    /// from the roster, leaving them unmessageable. That is strictly worse than the ghost being
    /// removed, and the Unknown fact is what stops it.</para>
    ///
    /// Uses a REAL <see cref="MessageBroker"/> with its databases isolated to temp files.
    /// </summary>
    public sealed class TerminalRosterLivenessTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public TerminalRosterLivenessTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_roster_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_roster_msg_{stamp}.db");
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
        /// A real process, started and still running, whose start time is readable — so it can be
        /// bound as a row's owner and then killed to manufacture a genuine corpse.
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
        public void A_terminal_whose_owner_process_is_dead_is_no_longer_listed()
        {
            using var broker = new MessageBroker();

            var child = StartBindableChild();
            int deadPid = child.Id;
            try
            {
                broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null, ownerPid: deadPid);

                // PRECONDITION: the row must really be OWNED by a verified incarnation, or killing the
                // child proves nothing and this fact would pass for the wrong reason.
                var bound = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
                Assert.Equal(deadPid, bound.OwnerPid);
                Assert.NotNull(bound.OwnerStartTime);
                Assert.Contains(broker.GetTerminals(), t => t.Name == "Robin");   // listed while alive

                child.Kill();
                Assert.True(child.WaitForExit(10000),
                    "The child did not exit within 10s, so the row's owner is not actually dead and "
                    + "this fact would otherwise fail for the wrong reason.");
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }

            // THE CLAIM: the ghost is gone from the Terminals list. This is the Owner's symptom.
            Assert.DoesNotContain(broker.GetTerminals(), t => t.Name == "Robin");

            // ...and it is a LISTING change, not a state change. The row is still there for anything
            // that needs the raw view; nothing was deleted or disconnected behind the caller's back.
            // (Reconciling the stored state is deliberately a separate item on this ticket.)
            Assert.Contains(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
        }

        [Fact]
        public void A_terminal_whose_owner_cannot_be_probed_is_STILL_listed()
        {
            // ⚠️ THE LOAD-BEARING FACT. Revert the filter from `== Dead` to `!= Alive` and this is the
            // only test in the suite that goes red — the dead-row fact above passes either way, because
            // a corpse is not Alive under both spellings.
            //
            // "Cannot verify" is NOT "gone". ResolveOwnerLiveness's own remarks record the Run 2 defect
            // where those were collapsed: an access-denied probe (MT unelevated while claude is
            // elevated, a protected process, a cross-session pid) made a LIVE owner read as a corpse.
            // Hiding Unknown would delete healthy terminals from the roster — the Owner could not see
            // or message a terminal that is sitting right there working.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null,
                                    ownerPid: Environment.ProcessId);

            // Manufacture the documented Unknown shape: a row carrying a pid but NO start time. Per
            // ResolveOwnerLiveness, "a row carrying a pid without one predates that rule. Unverifiable,
            // not provably gone." GetAllConnectedTerminals hands back the live row, not a copy.
            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
            Assert.NotNull(row.OwnerStartTime);      // precondition: there WAS one to remove
            row.OwnerStartTime = null;

            // The mutation above assumes GetAllConnectedTerminals hands back the LIVE row rather than
            // a copy. That holds today, but nothing pins it — and if it ever projects copies the write
            // is discarded, the row stays Alive, and this fact goes VACUOUSLY green while still
            // claiming to be the load-bearing guard against a two-state filter. Assert the write
            // actually landed on broker state.
            Assert.Null(Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin").OwnerStartTime);

            // THE CLAIM: unverifiable keeps its place in the list.
            Assert.Contains(broker.GetTerminals(), t => t.Name == "Robin");
        }

        [Fact]
        public void A_terminal_that_names_no_owner_at_all_is_STILL_listed()
        {
            // The Unowned state, and it is ordinary rather than exotic: every terminal already running
            // when this deploys reports no ownerPid, and spawned-agent and Oracle rows carry none at
            // all. A pid probe is meaningless for them and always will be — a gateway terminal on
            // another machine can never be probed from here. They must never be filtered out.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null, ownerPid: null);

            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
            Assert.Null(row.OwnerPid);               // precondition: genuinely Unowned

            Assert.Contains(broker.GetTerminals(), t => t.Name == "Robin");
        }

        [Fact]
        public void An_agent_whose_owner_is_dead_is_not_reported_online()
        {
            // The bug this closes: "online" had two meanings. GetTerminals answered it from live
            // terminals; four other sites answered it from profile.IsOnline, a STORED flag that is
            // set on registration and cleared only by an explicit disconnect. Nothing clears it for a
            // session that simply died.
            //
            // That is not cosmetic. GET /api/team/roster backs the get_team_roster MCP tool — the tool
            // AGENTS use to decide who to talk to — so a corpse reported as online invites an agent to
            // message a terminal that no longer exists.
            using var broker = new MessageBroker();

            var child = StartBindableChild();
            try
            {
                broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null, ownerPid: child.Id);
                Assert.Contains("Robin", broker.GetOnlineAgentNames());   // precondition: online while alive

                child.Kill();
                Assert.True(child.WaitForExit(10000),
                    "The child did not exit within 10s, so the owner is not actually dead and this "
                    + "fact would pass for the wrong reason.");
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }

            Assert.DoesNotContain("Robin", broker.GetOnlineAgentNames());
        }

        [Fact]
        public void An_agent_whose_owner_cannot_be_probed_is_STILL_reported_online()
        {
            // ⚠️ The same load-bearing shape as the listing facts above. GetOnlineAgentNames is
            // DERIVED from GetTerminals precisely so it inherits the three-state rule; the danger is
            // someone "optimising" it into its own liveness check that collapses Unknown into Dead.
            // That would drop healthy agents out of the roster the moment their pid cannot be probed
            // (MT unelevated while claude is elevated), and every other fact here would still pass.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null,
                                    ownerPid: Environment.ProcessId);

            var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
            Assert.NotNull(row.OwnerStartTime);
            row.OwnerStartTime = null;              // the documented Unknown shape

            // Same anti-vacuity guard as the listing fact — prove the write reached broker state.
            Assert.Null(Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin").OwnerStartTime);

            Assert.Contains("Robin", broker.GetOnlineAgentNames());
        }

        [Fact]
        public void Online_agents_are_exactly_the_listed_terminals()
        {
            // "Is X online?" must have ONE answer. It previously had two that disagreed, in the same
            // file: /api/team/profiles derived it from live terminals while /api/team/roster forty
            // lines below read the stored flag. This pins GetOnlineAgentNames as a VIEW of
            // GetTerminals rather than a second source of truth that is free to drift from it.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Robin", docId: null, channelPort: 8801, nonce: null, ownerPid: null);
            broker.RegisterTerminal("Wren", docId: "DW", channelPort: 8802, nonce: "NW");

            var listed = broker.GetTerminals().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var online = broker.GetOnlineAgentNames();

            Assert.True(listed.SetEquals(online),
                $"listed=[{string.Join(", ", listed.OrderBy(x => x))}] " +
                $"online=[{string.Join(", ", online.OrderBy(x => x))}]");
        }

        [Fact]
        public void Online_agent_lookup_ignores_case()
        {
            // The call sites compare names that come from profiles (DisplayName ?? Id) against names
            // that come from terminal rows. Those are not guaranteed to agree in case, and an ordinal
            // set would silently report a live agent as offline.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Robin", docId: "DR", channelPort: 8801, nonce: "NR");

            var online = broker.GetOnlineAgentNames();
            Assert.Contains("robin", online);
            Assert.Contains("ROBIN", online);
        }

        [Fact]
        public void Hidden_from_the_listing_does_NOT_mean_the_name_is_available()
        {
            // ⚠️ PIPELINE RUN 1 / DEBUGGER HIGH. This is the regression the roster filter introduced,
            // and it is why MainForm.PreRegisterTerminal reads GetAllConnectedTerminals().
            //
            // GetTerminals() drops a row whose owner is Dead. Gate (4) does NOT release that name if
            // the row carries a LaunchNonce — "held by its (immortal) nonce" (MessageBroker.cs:2613) —
            // and EVERY MT-launched row is nonce-bearing. So the two answers diverge, and MainForm's
            // name pool sat between them: it built takenNames from the listing, handed out a name the
            // registration gate then refused, and started the terminal with a NULL name. An unnamed
            // terminal is precisely the shape whose SessionEnd hook early-returns, so it became the
            // next ghost — and since the pool is scanned in fixed order, that one ghost captured
            // every subsequent tab until restart.
            //
            // The fix is a distinction, not a filter change: "is this name free" is a different
            // question from "should this appear in the Terminals list". This fact pins them apart.
            using var broker = new MessageBroker();

            var child = StartBindableChild();
            try
            {
                // An MT-LAUNCHED row: carries a docId AND a launch nonce, like every real terminal.
                broker.RegisterTerminal("Robin", docId: "D1", channelPort: 8801, nonce: "N1",
                                        ownerPid: child.Id);

                var bound = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
                Assert.Equal(child.Id, bound.OwnerPid);
                Assert.False(string.IsNullOrEmpty(bound.LaunchNonce),
                    "precondition: the row must be nonce-bearing, or this fact tests the wrong population");

                child.Kill();
                Assert.True(child.WaitForExit(10000), "child did not exit; the owner is not actually dead");
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }

            // The listing hides it — that is the Owner's symptom, fixed.
            Assert.DoesNotContain(broker.GetTerminals(), t => t.Name == "Robin");

            // ...but THE NAME IS STILL HELD. A different process claiming it is refused, because the
            // nonce holds the row regardless of liveness. This is what the name pool must see.
            var claim = broker.RegisterTerminal("Robin", docId: "D2", channelPort: 8802, nonce: "N2");
            Assert.False(claim.Success,
                "gate (4) must still refuse a nonce-bearing row's name after its owner dies — if this "
                + "ever passes, the filter and the gate agree again and MainForm may use either view.");

            // The raw view is therefore the honest source for name availability, and it still shows it.
            Assert.Contains(broker.GetAllConnectedTerminals(), t => t.Name == "Robin");
        }

        [Fact]
        public void Online_names_derived_from_a_caller_snapshot_match_the_zero_arg_overload()
        {
            // The two-overload split exists so a caller holding the rows does not take a SECOND,
            // independent snapshot — a terminal dying between two calls lands in one and not the
            // other, putting a single rendered payload in disagreement with itself. Pin that the
            // projection overload gives the same answer, so nobody "simplifies" the panels back to
            // two calls on the grounds that it reads more nicely.
            using var broker = new MessageBroker();

            broker.RegisterTerminal("Robin", docId: "DR", channelPort: 8801, nonce: "NR");
            broker.RegisterTerminal("Wren", docId: "DW", channelPort: 8802, nonce: "NW");

            var terminals = broker.GetTerminals();
            Assert.True(broker.GetOnlineAgentNames(terminals).SetEquals(broker.GetOnlineAgentNames()));

            // Degenerate inputs must not throw on a UI refresh path.
            Assert.Empty(broker.GetOnlineAgentNames(null));
            Assert.Empty(broker.GetOnlineAgentNames(new List<MultiTerminal.MCPServer.Models.TerminalInfo>()));
        }
    }
}
