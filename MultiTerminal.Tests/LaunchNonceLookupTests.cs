using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The launch-nonce gate on GitHub token minting (task b42b1883, item 2).
    ///
    /// <para>This lookup is the whole authentication story for the mint endpoint. MT's REST API is
    /// loopback and unauthenticated by design (established by task c9285d2a's security audit), so
    /// without it any local process could ask MultiTerminal for a token that comments and pushes code
    /// as the bot. It is a small method carrying a large amount of weight, which is why it has more
    /// tests than lines.</para>
    ///
    /// <para><b>The fact that matters is the empty-nonce one.</b> Rows legitimately carry an empty
    /// <c>LaunchNonce</c> — a terminal MT did not launch has no nonce to seed — so the obvious
    /// equality test would let a caller presenting <c>""</c> match every one of them. That is a
    /// "does the secret identify a terminal" question being answered as "are these two values equal",
    /// and it is the exact shape of bug the sibling ticket kept producing.</para>
    /// </summary>
    public sealed class LaunchNonceLookupTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _testMsgDbPath;

        public LaunchNonceLookupTests()
        {
            // MessageBroker lazily constructs its own TaskDatabase, and TaskDatabase.GetDatabasePath
            // falls back to %APPDATA%\multiterminal\multiterminal.db when MULTITERMINAL_TEST_DB is
            // unset. This class never set it, so every `dotnet test` run applied TaskDatabase's
            // migrations to the PRODUCTION database — invisible while migrations were additive, and
            // exposed the first time one rebuilt a table (task 77d1182f, pipeline Run 4: the live
            // user_inbox was rebuilt at 02:21Z, during a verifier's test run, while the deployed
            // binary was still pre-migration). Same isolation idiom as every other TaskDatabase test.
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_nonce_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);

            // Task 2ddfc32f: the comment above closed ONE door. MessageBroker also constructs a
            // MessageQueueDatabase, whose path is governed by a DIFFERENT variable — so this class went
            // on opening the production %APPDATA%\multiterminal\messages.db on every run, for exactly
            // the same reason and in the same file, until ProductionDataGuard refused it. The 77d1182f
            // census could not see this: it was keyed on MULTITERMINAL_TEST_DB and reported the file
            // clean. Two databases, two variables, one broker.
            _testMsgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_nonce_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _testMsgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            foreach (var p in new[]
            {
                _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm",
                _testMsgDbPath, _testMsgDbPath + "-wal", _testMsgDbPath + "-shm",
            })
            {
                // Best-effort: another test class running in parallel may still hold a handle, and a
                // failure to delete a temp file must not fail an otherwise-passing test.
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
        }

        [Fact]
        public void A_connected_terminals_nonce_resolves_to_that_terminal()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: "NONCE-ALICE");

            TerminalInfo found = broker.GetConnectedTerminalByLaunchNonce("NONCE-ALICE");

            Assert.NotNull(found);
            Assert.Equal("Alice", found.Name);
        }

        [Fact]
        public void An_empty_nonce_matches_nothing_even_though_rows_with_empty_nonces_exist()
        {
            // THE fact this gate exists for. "Bob" is an ordinary terminal MT did not launch, so it
            // carries no nonce. A naive `t.LaunchNonce == nonce` would match Bob for a caller presenting
            // "" — and the endpoint would then mint a bot token for anything that asked with no
            // credential at all, while looking like it had authenticated something.
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Bob", docId: "DB", nonce: null);

            Assert.Null(broker.GetConnectedTerminalByLaunchNonce(""));
            Assert.Null(broker.GetConnectedTerminalByLaunchNonce(null));

            // And Bob really is present and really has no nonce — otherwise the assertions above pass
            // trivially against an empty roster, which would be a vacuous test of exactly the kind this
            // codebase keeps catching.
            TerminalInfo bob = Assert.Single(broker.GetTerminals(), t => t.Name == "Bob");
            Assert.True(string.IsNullOrEmpty(bob.LaunchNonce));
        }

        [Fact]
        public void A_wrong_nonce_matches_nothing()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: "NONCE-ALICE");

            Assert.Null(broker.GetConnectedTerminalByLaunchNonce("NONCE-SOMEONE-ELSE"));
        }

        [Fact]
        public void A_nonce_is_matched_exactly_and_not_case_insensitively()
        {
            // Ordinal comparison, stated as a fact. A case-insensitive match would shrink the effective
            // keyspace of the secret for no benefit.
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: "NONCE-ALICE");

            Assert.Null(broker.GetConnectedTerminalByLaunchNonce("nonce-alice"));
        }

        [Fact]
        public void A_disconnected_terminals_nonce_stops_working()
        {
            // A closed terminal must not keep the ability to mint. The nonce outlives the session in
            // the row, so "is it connected" is the part that expires.
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: "NONCE-ALICE");
            Assert.NotNull(broker.GetConnectedTerminalByLaunchNonce("NONCE-ALICE"));

            broker.DisconnectTerminalByName("Alice");

            Assert.Null(broker.GetConnectedTerminalByLaunchNonce("NONCE-ALICE"));
        }

        [Fact]
        public void One_terminals_nonce_never_resolves_to_another()
        {
            // Attribution: the endpoint reports which terminal minted, and that is only meaningful if
            // the mapping is exact when several terminals are live at once.
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Alice", docId: "DA", nonce: "NONCE-ALICE");
            broker.RegisterTerminal("Charlie", docId: "DC", nonce: "NONCE-CHARLIE");

            Assert.Equal("Alice", broker.GetConnectedTerminalByLaunchNonce("NONCE-ALICE")?.Name);
            Assert.Equal("Charlie", broker.GetConnectedTerminalByLaunchNonce("NONCE-CHARLIE")?.Name);
        }
    }
}
