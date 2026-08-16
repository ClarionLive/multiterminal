using System;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GH#7 follow-up regression tests (ticket f6800613): "messages delivered with EMPTY BODIES".
    ///
    /// The defect being pinned — reproduced live against a running build before the fix:
    /// (1) <c>/api/messaging/send</c> with <c>"message":""</c> returned
    ///     <c>{"success":true,"delivered":true}</c> and persisted a row with <c>content=''</c>.
    ///     Every render path then mangled it: the channel server's
    ///     <c>msg.message || msg.content || body</c> chain treats "" as falsy and dumps the RAW
    ///     envelope JSON at the recipient; the inbox hook silently drops it with no trace.
    ///     So the sender was told "delivered" while the recipient got nothing usable.
    /// (2) Omitting <c>message</c> entirely NRE'd on the first log line of
    ///     <c>SendMessage</c> (<c>content.Substring(...)</c>), surfacing as an opaque HTTP 500.
    ///
    /// Both now reject at the broker — the single choke point every sender funnels through —
    /// with <c>Success=false</c>, which mcp/index.js renders as the honest
    /// "❌ NOT sent — &lt;error&gt;. Nothing was queued; nothing will retry." text.
    ///
    /// Same isolated-temp-DB harness as <see cref="MessageDeliveryHonestyTests"/>.
    /// </summary>
    public sealed class EmptyMessageBodyTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public EmptyMessageBodyTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_f68_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_f68_msg_{stamp}.db");
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

        [Theory]
        [InlineData("")]           // the live-reproduced case: persisted content='' and claimed delivered
        [InlineData("   ")]        // whitespace-only renders identically blank to a human
        [InlineData("\t\r\n")]
        public async Task Empty_body_is_rejected_and_nothing_is_queued(string body)
        {
            using var broker = new MessageBroker();
            var from = broker.RegisterTerminal("SenderE", docId: "E1");
            broker.RegisterTerminal("ReceiverE", docId: "E2");

            // Callback would report success — proving the rejection happens BEFORE delivery
            // rather than being an artifact of a failing transport.
            broker.OnMessageDelivery = (_, _, _, _) => Task.FromResult(true);

            var result = await broker.SendMessage(from.TerminalId, "ReceiverE", body);

            Assert.False(result.Success);
            Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);

            // MessageId is assigned only after EnqueueMessage, so a null id is the observable
            // proof that NOTHING was persisted — the pre-fix path wrote a content='' row.
            Assert.Null(result.MessageId);

            var pendingCounts = broker.GetPendingMessageCounts();
            Assert.False(
                pendingCounts.TryGetValue("ReceiverE", out var count) && count > 0,
                "a rejected empty-body send must not leave a queued row behind");
        }

        [Fact]
        public async Task Null_body_is_rejected_instead_of_throwing()
        {
            // Pre-fix this threw NullReferenceException on the first statement of SendMessage
            // (content.Substring(...) in the ENTRY log line), which the REST layer surfaced as
            // an opaque HTTP 500 with no actionable error for the caller.
            using var broker = new MessageBroker();
            var from = broker.RegisterTerminal("SenderN", docId: "N1");
            broker.RegisterTerminal("ReceiverN", docId: "N2");

            var result = await broker.SendMessage(from.TerminalId, "ReceiverN", null);

            Assert.False(result.Success);
            Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.MessageId);
        }

        [Fact]
        public async Task Normal_body_still_sends_unchanged()
        {
            // Negative fixture: the guard must reject only genuinely-empty bodies, not smother
            // legitimate traffic. Without this, "reject everything" would pass the tests above.
            using var broker = new MessageBroker();
            var from = broker.RegisterTerminal("SenderP", docId: "P1");
            broker.RegisterTerminal("ReceiverP", docId: "P2");

            broker.OnMessageDelivery = (_, _, _, _) => Task.FromResult(true);

            var result = await broker.SendMessage(from.TerminalId, "ReceiverP", "a real message");

            Assert.True(result.Success);
            Assert.True(result.Delivered);
            Assert.NotNull(result.MessageId);
        }

        [Fact]
        public async Task Body_of_only_punctuation_or_a_single_char_is_not_treated_as_empty()
        {
            // Guards the boundary: IsNullOrWhiteSpace must not be widened into "looks
            // meaningless". A one-character reply ("?") is a legitimate message.
            using var broker = new MessageBroker();
            var from = broker.RegisterTerminal("SenderQ", docId: "Q1");
            broker.RegisterTerminal("ReceiverQ", docId: "Q2");

            broker.OnMessageDelivery = (_, _, _, _) => Task.FromResult(true);

            var result = await broker.SendMessage(from.TerminalId, "ReceiverQ", "?");

            Assert.True(result.Success);
            Assert.NotNull(result.MessageId);
        }

        [Fact]
        public async Task Empty_body_check_precedes_recipient_lookup()
        {
            // Documents the deliberate ordering: the body guard sits ABOVE the terminal
            // lookups, so an empty send to an unknown recipient reports the empty body rather
            // than "recipient not found". Pinned so a future reorder is a conscious choice —
            // both errors are truthful, but callers key off the text.
            using var broker = new MessageBroker();
            var from = broker.RegisterTerminal("SenderR", docId: "R1");

            var result = await broker.SendMessage(from.TerminalId, "NoSuchAgent", "");

            Assert.False(result.Success);
            Assert.Contains("empty", result.Error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
