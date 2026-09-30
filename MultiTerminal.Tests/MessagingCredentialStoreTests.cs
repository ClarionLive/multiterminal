using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 0ff1b520, item 3 — the broker half of capturing a session's native messaging ingress.
    ///
    /// The item's wording is what these tests are answerable to: the token "grants injection into a
    /// live session", so it must never be logged, must not sit in plain SQLite, and must be cleared
    /// when the session ends — and "the clearing and the non-logging must each be asserted by a test;
    /// 'we were careful' is not a control."
    ///
    /// Division of labour between the two repositories, because item 3 spans both:
    ///   - NON-LOGGING of the captured value is asserted on the hook side, behaviourally, by
    ///     unit-messaging-credentials.js in the multiterminal-marketplace repo (a sentinel token is
    ///     driven through every path and the captured stdout/stderr/dtrace is searched for it).
    ///   - CLEARING is asserted here, and so is the structural half of non-disclosure: the credential
    ///     type refuses to print itself.
    ///
    /// The clearing test that matters is <see cref="Disconnect_endpoint_clears_the_credential"/>: it
    /// drives the ACTUAL endpoint the SessionEnd hook calls, not the store method underneath it.
    /// Testing <c>Clear</c> alone would pass just as happily if nobody had wired it into disconnect,
    /// which is the whole thing that could realistically go wrong.
    /// </summary>
    public sealed class MessagingCredentialStoreTests : IDisposable
    {
        private const string Socket = @"\\.\pipe\LOCAL\cc-msg-0123456789abcdef0123456789abcdef";
        private const string Token = "tokenvalue0123456789abcdef";

        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public MessagingCredentialStoreTests()
        {
            // MessageBroker constructs a TaskDatabase, and ProductionDataGuard refuses to let a test
            // touch the real one. Same per-instance temp-DB pattern the other broker tests use.
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_0ff1_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_0ff1_msg_{stamp}.db");
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
        public void Stored_credentials_come_back()
        {
            var store = new MessagingCredentialStore();

            Assert.True(store.Store("Alice", "session-1", Socket, Token));
            Assert.True(store.TryGet("Alice", out var credential));
            Assert.Equal(Socket, credential.Socket);
            Assert.Equal(Token, credential.Token);
            Assert.Equal("session-1", credential.SessionId);
        }

        [Fact]
        public void Lookup_is_case_insensitive_and_untrimmed_like_the_broker()
        {
            // MessageBroker keys every name collection with StringComparer.OrdinalIgnoreCase and does
            // NOT trim. This store must agree exactly. A lookup more permissive than the broker's
            // would answer for a terminal the broker resolves as a different one; a stricter one
            // would miss terminals the broker happily routes to. Both directions are pinned here, so
            // "fixing" one by adding a Trim() fails loudly instead of silently merging two identities.
            var store = new MessagingCredentialStore();
            store.Store("Alice", "s", Socket, Token);

            Assert.True(store.Has("alice"));
            Assert.True(store.Has("ALICE"));
            Assert.False(store.Has(" Alice"));
            Assert.False(store.Has("Alice "));
        }

        [Fact]
        public void Re_storing_replaces_rather_than_duplicating()
        {
            // The normal case after a /clear: same terminal name, brand new session, new pipe.
            var store = new MessagingCredentialStore();
            store.Store("Alice", "session-1", Socket, Token);
            store.Store("Alice", "session-2", Socket, "replacementtoken0123456789");

            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet("Alice", out var credential));
            Assert.Equal("session-2", credential.SessionId);
            Assert.Equal("replacementtoken0123456789", credential.Token);
        }

        [Fact]
        public void Blank_inputs_are_refused()
        {
            var store = new MessagingCredentialStore();

            Assert.False(store.Store(null, "s", Socket, Token));
            Assert.False(store.Store("", "s", Socket, Token));
            Assert.False(store.Store("   ", "s", Socket, Token));
            Assert.False(store.Store("Alice", "s", null, Token));
            Assert.False(store.Store("Alice", "s", Socket, null));
            Assert.False(store.Store("Alice", "s", Socket, "   "));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void Clear_removes_and_is_idempotent()
        {
            var store = new MessagingCredentialStore();
            store.Store("Alice", "s", Socket, Token);

            Assert.True(store.Clear("Alice"));
            Assert.False(store.Has("Alice"));

            // The disconnect path can run more than once for one terminal; a second clear is not an
            // error and must not throw.
            Assert.False(store.Clear("Alice"));
        }

        [Fact]
        public void Credential_does_not_print_its_own_token()
        {
            // Structural, not conventional. ToString is called by log formatters, exception messages
            // and debugger dumps without anyone deciding to, so a credential that prints itself will
            // eventually be printed. This is the half of "never log the token" that does not depend
            // on every future caller remembering.
            var store = new MessagingCredentialStore();
            store.Store("Alice", "session-1", Socket, Token);
            store.TryGet("Alice", out var credential);

            string rendered = credential.ToString();

            Assert.DoesNotContain(Token, rendered, StringComparison.Ordinal);
            Assert.Contains("<redacted>", rendered, StringComparison.Ordinal);
            Assert.Contains("session-1", rendered, StringComparison.Ordinal);
        }

        [Fact]
        public void Disconnect_endpoint_clears_the_credential()
        {
            // Drives the endpoint the SessionEnd hook actually POSTs to. If someone adds the clear to
            // the store but never wires it into disconnect, the store-level test above still passes
            // and this one fails — which is the point of having both.
            // The broker's own store, as production wires it: since ticket 9a731cda item 4 the clear
            // is the broker teardown's, not a separate one in the controller.
            using var broker = new MessageBroker();
            var store = broker.MessagingCredentials;
            var controller = new MessagingController(broker, store);

            store.Store("Alice", "session-1", Socket, Token);
            Assert.True(store.Has("Alice"));

            controller.DisconnectTerminal(new DisconnectTerminalRequest { Name = "Alice" });

            Assert.False(store.Has("Alice"));
        }

        [Fact]
        public void Disconnecting_one_terminal_leaves_the_others_alone()
        {
            // The discriminator. Without it, a handler that called ClearAll() — or cleared by a
            // predicate that matched everything — would satisfy the test above and quietly cut every
            // other terminal's ingress each time any one session ended. Two live entries, one
            // disconnect, and the survivor is what distinguishes a targeted clear from a blanket one.
            using var broker = new MessageBroker();
            var store = broker.MessagingCredentials;
            var controller = new MessagingController(broker, store);

            store.Store("Alice", "session-1", Socket, Token);
            store.Store("Bob", "session-2", Socket, "bobstoken0123456789abcdef");

            controller.DisconnectTerminal(new DisconnectTerminalRequest { Name = "Alice" });

            Assert.False(store.Has("Alice"));
            Assert.True(store.Has("Bob"));
            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void Store_endpoint_rejects_incomplete_bodies()
        {
            using var broker = new MessageBroker();
            var store = new MessagingCredentialStore();
            var controller = new MessagingController(broker, store);

            controller.StoreMessagingCredentials(new StoreMessagingCredentialsRequest { Name = "Alice", Socket = Socket });
            controller.StoreMessagingCredentials(new StoreMessagingCredentialsRequest { Name = "Alice", Token = Token });
            controller.StoreMessagingCredentials(new StoreMessagingCredentialsRequest { Socket = Socket, Token = Token });

            Assert.Equal(0, store.Count);
        }
    }
}
