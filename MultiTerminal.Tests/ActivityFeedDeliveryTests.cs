using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Delivery-aware activity recording (task 7d140c8b). The worktree janitor dedups on
    /// whether an activity line actually persisted (MessageBroker.RecordActivity returns a
    /// bool that the janitor uses to decide whether to remember a key). A post-INSERT
    /// notification failure must therefore NOT be misreported as a persistence failure —
    /// otherwise the janitor would re-insert the same actionable line every sweep. These
    /// tests guard ActivityFeedService's best-effort <c>ActivityRecorded</c> dispatch: the
    /// row is committed before subscribers run, so a throwing subscriber cannot escape.
    /// </summary>
    public sealed class ActivityFeedDeliveryTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _testMsgDbPath;
        private readonly TaskDatabase _taskDb;
        private readonly ActivityFeedService _activity;

        public ActivityFeedDeliveryTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_actfeed_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);

            // Task 2ddfc32f: two of this class's facts construct a MessageBroker, which constructs a
            // MessageQueueDatabase — a SECOND production database behind a SECOND variable. Unset, it
            // resolved to the live %APPDATA%\multiterminal\messages.db on every run until
            // ProductionDataGuard refused it. Setting MULTITERMINAL_TEST_DB alone was never enough.
            _testMsgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_actfeed_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _testMsgDbPath);
            _taskDb = new TaskDatabase();            // creates the base schema on the shared test DB
            _activity = new ActivityFeedService();   // owns its own connection; creates the activity_feed table
        }

        public void Dispose()
        {
            _activity?.Dispose();
            _taskDb?.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            foreach (var p in new[]
            {
                _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm",
                _testMsgDbPath, _testMsgDbPath + "-wal", _testMsgDbPath + "-shm",
            })
            {
                // Best-effort: a parallel test class may still hold a handle, and failing to delete a
                // temp file must not fail an otherwise-passing test.
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
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void RecordGeneralActivity_WhenSubscriberThrows_StillPersistsAndDoesNotThrow()
        {
            // Subscriber blows up AFTER the row is committed; it must not escape the call,
            // so the caller (broker → janitor) sees a successful, deduplicated insert.
            _activity.ActivityRecorded += (s, e) => throw new InvalidOperationException("subscriber down");

            var id = _activity.RecordGeneralActivity(
                "worktree_janitor_pending_merge", "Janitor", "Branch task/x still alive", projectId: "projA");

            Assert.True(id > 0); // committed despite the throwing subscriber (no exception propagated)
            var rows = _activity.GetRecentActivities(50, projectId: "projA");
            Assert.Contains(rows, r => r.ActivityType == "worktree_janitor_pending_merge");
        }

        [Fact]
        public void RecordGeneralActivity_Normal_PersistsAndNotifiesOnce()
        {
            int notified = 0;
            _activity.ActivityRecorded += (s, e) => notified++;

            var id = _activity.RecordGeneralActivity(
                "worktree_janitor_sweep", "Janitor", "sweep summary", projectId: "projB");

            Assert.True(id > 0);
            Assert.Equal(1, notified); // exactly one notification for one recorded row
            Assert.Single(_activity.GetRecentActivities(50, projectId: "projB"));
        }

        [Fact]
        public void RecordActivity_NullFeedService_ReportsNotDelivered()
        {
            // Startup race: the janitor's first sweep can fire before the REST host has wired
            // ActivityFeedService. RecordActivity must report NOT delivered (false) so the
            // delivery-aware janitor retries once the feed is available, rather than remembering
            // a key that was never durably stored (codex security, task 7d140c8b).
            using var broker = new MessageBroker();
            broker.ActivityFeedService = null;

            var delivered = broker.RecordActivity(new ActivityEvent
            {
                Terminal = "Janitor", Type = "worktree", Action = "janitor_pending_merge", Content = "Branch task/x still alive",
            });

            Assert.False(delivered);
        }

        [Fact]
        public void RecordActivity_WithFeedService_ReportsDelivered()
        {
            using var broker = new MessageBroker();
            broker.ActivityFeedService = _activity;

            var delivered = broker.RecordActivity(new ActivityEvent
            {
                Terminal = "Janitor", Type = "worktree", Action = "janitor_sweep", Content = "sweep summary",
            });

            Assert.True(delivered); // durably stored → janitor may dedup this key
        }
    }
}
