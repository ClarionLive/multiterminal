using System;
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
    }
}
