using System;
using System.Data.SQLite;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GH#7 regression tests (ticket 405273fd): "send reports success, recipient never receives".
    ///
    /// The defect chain being pinned:
    /// (1) <c>SendResult</c> could not distinguish CONFIRMED Tier-2 delivery from
    ///     accepted-and-queued, so silent losses rendered as "✅ sent" to the sender;
    /// (2) a failed delivery must leave the queue row RETRYABLE (Tier 3), not laundered
    ///     into "delivered";
    /// (3) <c>InboxFileWriter.WriteMessage</c> had no per-id dedup, so the new
    ///     buffer-then-retry path would append a duplicate row on every Tier-3 attempt.
    /// Uses a REAL <see cref="MessageBroker"/> with its databases isolated to temp files
    /// (same pattern as <see cref="BrokerPlaceholderAdoptionNonceTests"/>).
    /// </summary>
    public sealed class MessageDeliveryHonestyTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public MessageDeliveryHonestyTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_gh7_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_gh7_msg_{stamp}.db");
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
        public async Task Send_with_failing_callback_reports_not_delivered_and_row_stays_retryable()
        {
            using var broker = new MessageBroker();
            var from = broker.RegisterTerminal("SenderA", docId: "D1");
            var to = broker.RegisterTerminal("ReceiverA", docId: "D2");

            // Tier-2 callback fails (channel down / recipient gone) — the pre-fix code path
            // still returned an undifferentiated Success=true to the sender.
            broker.OnMessageDelivery = (_, _, _, _) => Task.FromResult(false);

            var result = await broker.SendMessage(from.TerminalId, "ReceiverA", "hello?");

            Assert.True(result.Success);              // accepted (persisted + queued) — unchanged
            Assert.False(result.Delivered);           // but NOT claimed delivered — the GH#7 fix
            Assert.NotNull(result.MessageId);

            // The queue row must remain visible to Tier-3 retry (status pending, retry_count 1).
            var pendingCounts = broker.GetPendingMessageCounts();
            Assert.True(
                pendingCounts.TryGetValue("ReceiverA", out var count) && count == 1,
                $"expected 1 retryable message for ReceiverA, got: {string.Join(", ", pendingCounts)}");
        }

        [Fact]
        public async Task Send_with_successful_callback_reports_delivered_and_leaves_nothing_pending()
        {
            using var broker = new MessageBroker();
            var from = broker.RegisterTerminal("SenderB", docId: "D3");
            broker.RegisterTerminal("ReceiverB", docId: "D4");

            broker.OnMessageDelivery = (_, _, _, _) => Task.FromResult(true);

            var result = await broker.SendMessage(from.TerminalId, "ReceiverB", "hi!");

            Assert.True(result.Success);
            Assert.True(result.Delivered);            // confirmed push → the ✅ path is truthful

            var pendingCounts = broker.GetPendingMessageCounts();
            Assert.False(
                pendingCounts.TryGetValue("ReceiverB", out var count) && count > 0,
                "a confirmed-delivered message must not linger as retryable");
        }

        [Fact]
        public void Inbox_write_same_id_twice_appends_once()
        {
            // Unique per-run terminal name so the shared %APPDATA% inbox dir can't collide
            // with a real agent's inbox; file is removed in finally.
            string terminal = "gh7-test-" + Guid.NewGuid().ToString("N");
            string inboxFile = Path.Combine(InboxFileWriter.GetInboxDirectory(), terminal + ".json");
            try
            {
                Assert.True(InboxFileWriter.WriteMessage(terminal, "msg-1", "SenderX", "first attempt"));
                // Tier-3 retry re-buffers the SAME message id — must be a no-op, not a duplicate.
                Assert.True(InboxFileWriter.WriteMessage(terminal, "msg-1", "SenderX", "first attempt"));

                using var doc = JsonDocument.Parse(File.ReadAllText(inboxFile));
                Assert.Equal(1, doc.RootElement.GetArrayLength());
            }
            finally
            {
                if (File.Exists(inboxFile)) File.Delete(inboxFile);
            }
        }

        [Fact]
        public void Inbox_write_distinct_ids_append_both()
        {
            // Negative fixture for the dedup guard: it must key on messageId, not smother
            // legitimate consecutive messages.
            string terminal = "gh7-test-" + Guid.NewGuid().ToString("N");
            string inboxFile = Path.Combine(InboxFileWriter.GetInboxDirectory(), terminal + ".json");
            try
            {
                Assert.True(InboxFileWriter.WriteMessage(terminal, "msg-1", "SenderX", "one"));
                Assert.True(InboxFileWriter.WriteMessage(terminal, "msg-2", "SenderX", "two"));

                using var doc = JsonDocument.Parse(File.ReadAllText(inboxFile));
                Assert.Equal(2, doc.RootElement.GetArrayLength());
            }
            finally
            {
                if (File.Exists(inboxFile)) File.Delete(inboxFile);
            }
        }
    }
}
