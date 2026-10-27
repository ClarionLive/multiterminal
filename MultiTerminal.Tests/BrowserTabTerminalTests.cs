using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 2b31dc94: a browser-tab call with no terminal id threw ArgumentNullException from
    /// MessageBroker.GetTerminal, and POST /api/browser-tabs/open answered a bare 500. A blank id now names
    /// no terminal, so each route reports "not found" (the controller's 400) instead of throwing.
    /// </summary>
    public sealed class BrowserTabTerminalTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _testMsgDbPath;

        public BrowserTabTerminalTests()
        {
            // A MessageBroker opens both the task database and the message queue database (task 2ddfc32f).
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_browsertab_{Guid.NewGuid():N}.db");
            _testMsgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_browsertab_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _testMsgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[]
            {
                _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm",
                _testMsgDbPath, _testMsgDbPath + "-wal", _testMsgDbPath + "-shm",
            })
            {
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

        // Only null threw before the fix (falsified: removing the guard turns the null case and both
        // route facts red). An empty id is a legal dictionary key that matched nothing; that case
        // pins the answer, not the guard.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_blank_id_names_no_terminal(string id)
        {
            using var broker = new MessageBroker();
            Assert.Null(broker.GetTerminal(id));
        }

        [Fact]
        public void Opening_a_tab_with_no_terminal_fails_with_a_message_instead_of_throwing()
        {
            using var broker = new MessageBroker();
            var (success, tabId, error) = broker.OpenBrowserTab(null, "t", null, "<p>x</p>");
            Assert.False(success);
            Assert.Null(tabId);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void Updating_or_closing_a_tab_with_no_terminal_fails_instead_of_throwing()
        {
            using var broker = new MessageBroker();
            Assert.False(broker.SetBrowserContent(null, "tab", "t", null, "<p>x</p>").success);
            Assert.False(broker.CloseBrowserTab(null, "tab").success);
        }
    }
}
