using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 0ff1b520 items 14 and 15: native messaging credentials die with the terminal on every
    /// broker teardown path, and their arrival is announced (by name only) so the Oracle bootstrap no
    /// longer depends on a channel port.
    ///
    /// Real <see cref="MessageBroker"/> with temp databases, and for the reap path a real child
    /// process killed to make a genuinely Dead owner, as in <see cref="TerminalLivenessReaperTests"/>.
    /// </summary>
    public sealed class MessagingCredentialTeardownTests : IDisposable
    {
        private const string Socket = @"\\.\pipe\LOCAL\cc-msg-0123456789abcdef";
        private const string OldToken = "old-token-0123456789abcdef";
        private const string NewToken = "new-token-fedcba9876543210";

        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public MessagingCredentialTeardownTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_credtear_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_credtear_msg_{stamp}.db");
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

        /// <summary>Registers <paramref name="name"/> owned by a real child process, then kills it.</summary>
        private static TerminalInfo RegisterWithDeadOwner(MessageBroker broker, string name, string docId = null)
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
                Assert.True(MessageBroker.TryGetProcessStartTime(child.Id, out _));
                broker.RegisterTerminal(name, docId: docId, channelPort: 8801, ownerPid: child.Id);

                var row = Assert.Single(broker.GetAllConnectedTerminals(), t => t.Name == name);
                Assert.Equal(child.Id, row.OwnerPid);

                child.Kill();
                Assert.True(child.WaitForExit(10000), "The owner did not die, so a reap proves nothing.");
                return row;
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }
        }

        // ─── Item 14: teardown clears ───────────────────────────────────────────────────────────

        [Fact]
        public void A_reaped_terminal_loses_its_credentials_and_nobody_else_does()
        {
            using var broker = new MessageBroker();
            var store = broker.MessagingCredentials;

            RegisterWithDeadOwner(broker, "Diana");
            store.Store("Diana", "session-d", Socket, OldToken);
            store.Store("Bob", "session-b", Socket, NewToken);

            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());

            Assert.False(store.Has("Diana"), "A reaped session's token for a dead pipe must not outlive it.");
            Assert.True(store.Has("Bob"), "A targeted clear, never ClearAll: Bob's session is alive.");
        }

        [Fact]
        public void DisconnectTerminalByName_clears_the_credentials_itself()
        {
            // Called on the broker directly, NOT through MessagingController, whose disconnect action
            // clears on its own. This proves the broker path, which is what non-REST callers reach.
            using var broker = new MessageBroker();
            var store = broker.MessagingCredentials;

            broker.RegisterTerminal("Eve", docId: "doc-eve");
            store.Store("Eve", "session-e", Socket, OldToken);

            broker.DisconnectTerminalByName("Eve");

            Assert.False(store.Has("Eve"));
        }

        [Fact]
        public void UnregisterTerminal_clears_the_credentials()
        {
            // The tab-close path (TerminalDocument / MainForm / close_helper).
            using var broker = new MessageBroker();
            var store = broker.MessagingCredentials;

            broker.RegisterTerminal("Frank", docId: "doc-frank");
            store.Store("Frank", "session-f", Socket, OldToken);

            broker.UnregisterTerminal("doc-frank");

            Assert.False(store.Has("Frank"));
        }

        [Fact]
        public void A_same_name_session_that_stores_during_the_teardown_keeps_its_credentials()
        {
            // The relaunch race: the dead Diana is being reaped while a NEW Diana's SessionStart hook
            // posts its ingress. TerminalDisconnected is raised between the teardown's decision (under
            // the lock) and its clear, so storing from a subscriber lands exactly in that gap,
            // deterministically. A plain name-keyed Clear deletes the new session's token here and it
            // never gets native delivery again, because the hook posts only once per session.
            using var broker = new MessageBroker();
            var store = broker.MessagingCredentials;

            RegisterWithDeadOwner(broker, "Diana");
            store.Store("Diana", "session-old", Socket, OldToken);
            broker.TerminalDisconnected += (_, t) => store.Store(t.Name, "session-new", Socket, NewToken);

            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());

            Assert.True(store.TryGet("Diana", out var survivor), "The new session's ingress was cleared by the old session's teardown.");
            Assert.Equal("session-new", survivor.SessionId);
        }

        [Fact]
        public void Closing_an_already_reaped_tab_does_not_clear_a_relaunched_sessions_credentials()
        {
            // The d1151661 shape, applied to credentials: after the reaper tore down the dead row and
            // Diana relaunched, closing the OLD tab must not run a second name-keyed teardown.
            using var broker = new MessageBroker();
            var store = broker.MessagingCredentials;

            RegisterWithDeadOwner(broker, "Diana", docId: "doc-old");
            store.Store("Diana", "session-old", Socket, OldToken);
            Assert.Equal(new[] { "Diana" }, broker.ReapDeadOwnerTerminals());
            Assert.False(store.Has("Diana"));  // precondition: the reap cleared the old one

            store.Store("Diana", "session-new", Socket, NewToken);
            broker.UnregisterTerminal("doc-old");

            Assert.True(store.Has("Diana"));
        }

        [Fact]
        public void Disposing_the_broker_clears_every_credential()
        {
            var broker = new MessageBroker();
            var store = broker.MessagingCredentials;
            store.Store("Alice", "s1", Socket, OldToken);
            store.Store("Bob", "s2", Socket, NewToken);

            broker.Dispose();

            Assert.Equal(0, store.Count);
        }

        // ─── Item 15: the arrival event and the bootstrap gate ─────────────────────────────────

        [Fact]
        public void Storing_credentials_raises_the_event_with_the_name_only()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            var raised = new List<string>();
            broker.MessagingCredentialsStored += (_, name) => raised.Add(name);

            var result = controller.StoreMessagingCredentials(new StoreMessagingCredentialsRequest
            {
                Name = "Oracle", SessionId = "session-o", Socket = Socket, Token = OldToken,
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(new[] { "Oracle" }, raised);
        }

        [Fact]
        public void A_rejected_store_raises_nothing()
        {
            using var broker = new MessageBroker();
            var controller = new MessagingController(broker, broker.MessagingCredentials);
            var raised = new List<string>();
            broker.MessagingCredentialsStored += (_, name) => raised.Add(name);

            controller.StoreMessagingCredentials(new StoreMessagingCredentialsRequest { Name = "Oracle", Socket = Socket });

            Assert.Empty(raised);
        }

        [Fact]
        public void The_gate_opens_once_for_Oracle_and_never_for_anyone_else()
        {
            var gate = new OracleBootstrapGate();

            Assert.False(gate.TryClaim("Alice"));
            Assert.False(gate.TryClaim(null));
            Assert.False(gate.IsClaimed, "A non-Oracle name must not consume the claim.");

            Assert.True(gate.TryClaim("oracle"));   // case-insensitive, like every Oracle check in MT
            Assert.False(gate.TryClaim("Oracle"));  // the second trigger for the same startup

            gate.Release();                          // the first delivery did not arrive
            Assert.True(gate.TryClaim("Oracle"));
        }

        [Fact]
        public void Two_triggers_racing_claim_the_bootstrap_exactly_once()
        {
            // Both triggers arrive on REST thread-pool threads, and for one Oracle startup both
            // normally fire. Many rounds, each releasing all racers at once.
            // PROBABILISTIC, measured not assumed: with the claim swapped for a plain check-then-set,
            // this went red in 2 of 3 runs and passed once; with a SpinWait widening the window
            // between the check and the set, it went red every run. So it catches a regression
            // usually, not always — a green run here is not proof of atomicity. The proof is the
            // single Interlocked.Exchange in OracleBootstrapGate.TryClaim.
            const int Rounds = 2000;
            const int Racers = 4;
            for (int round = 0; round < Rounds; round++)
            {
                var gate = new OracleBootstrapGate();
                using var start = new Barrier(Racers);
                int wins = 0;
                // Dedicated threads, not the pool: a Barrier across pool tasks can wait on thread
                // injection, and a timed Join fails instead of hanging the suite.
                var racers = Enumerable.Range(0, Racers).Select(_ => new Thread(() =>
                {
                    if (!start.SignalAndWait(10000)) return;
                    if (gate.TryClaim("Oracle")) Interlocked.Increment(ref wins);
                })).ToArray();
                foreach (var racer in racers) racer.Start();
                Assert.All(racers, racer => Assert.True(racer.Join(10000), "Racer did not finish."));

                Assert.Equal(1, wins);
            }
        }
    }
}
