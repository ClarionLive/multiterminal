using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// A taskless inbox message must be storable and readable (task 77d1182f, pipeline Run 3).
    ///
    /// <para>THE DEFECT THIS PINS. <c>user_inbox.task_id</c> was declared <c>NOT NULL</c> because
    /// every inbox message used to be a task notification. Two callers write taskless rows — the
    /// spawn path's "your helper never came alive, its job was NOT delivered" message and
    /// <c>JanitorAlertService</c>'s nullable <c>relatedTaskId</c> — and both silently lost them:
    /// <c>SaveInboxMessage</c> bound the null, SQLite refused the INSERT, and the broker swallowed
    /// the exception into a <c>Success=false</c> nobody checked. 1001 green tests did not notice
    /// because the inbox stub in the test host returns success unconditionally. These facts go
    /// through the REAL <see cref="TaskDatabase"/> on a temp SQLite file — the only way the
    /// constraint can be exercised at all.</para>
    /// </summary>
    public sealed class InboxTasklessMessageTests : IDisposable
    {
        private readonly string _testDbPath;

        public InboxTasklessMessageTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_inboxnull_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            foreach (var p in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(p)) File.Delete(p);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
        }

        [Fact]
        public void Taskless_spawn_failed_message_is_stored_and_read_back()
        {
            using var db = new TaskDatabase();

            var message = new InboxMessage
            {
                UserId = "Alice",
                TaskId = null,
                Type = "spawn_failed",
                Summary = "Helper SpawnProbe never came alive: registered no channel within 120s. Its job (312 chars) was NOT delivered.",
                CreatedBy = "MultiTerminal",
            };

            db.SaveInboxMessage(message); // used to throw "NOT NULL constraint failed: user_inbox.task_id"

            var inbox = db.GetInboxMessages("Alice");
            var stored = Assert.Single(inbox, m => m.Id == message.Id);
            Assert.Null(stored.TaskId);
            Assert.Equal("spawn_failed", stored.Type);
            Assert.Contains("NOT delivered", stored.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public void Migration_rebuilds_a_pre_existing_NOT_NULL_inbox_and_keeps_its_rows()
        {
            // Build the database the way every installation before this migration has it: the
            // original CREATE TABLE with task_id NOT NULL, and one real (tasked) row in it. Then let
            // TaskDatabase run its migrations over it. The rebuild must keep the row, drop the
            // constraint, and leave every index in place.
            using (var raw = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                raw.Open();
                using var create = new SQLiteCommand(
                    @"CREATE TABLE user_inbox (
                        id TEXT PRIMARY KEY,
                        user_id TEXT NOT NULL,
                        task_id TEXT NOT NULL,
                        task_title TEXT,
                        checklist_item_index INTEGER,
                        checklist_item_name TEXT,
                        type TEXT NOT NULL,
                        summary TEXT NOT NULL,
                        created_at DATETIME NOT NULL,
                        created_by TEXT NOT NULL,
                        read_at DATETIME,
                        reply_text TEXT,
                        replied_at DATETIME
                    );
                    CREATE INDEX idx_inbox_user ON user_inbox(user_id);
                    INSERT INTO user_inbox (id, user_id, task_id, type, summary, created_at, created_by)
                    VALUES ('legacy01', 'Alice', 'task-123', 'task_assigned', 'legacy row', '2026-01-01T00:00:00Z', 'Bob');",
                    raw);
                create.ExecuteNonQuery();
            }

            using var db = new TaskDatabase(); // runs MigrateUserInboxTaskIdNullable

            // The constraint is gone — asserted on the actual schema, not on the migration ledger.
            using (var raw = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                raw.Open();
                using var info = new SQLiteCommand("PRAGMA table_info(user_inbox)", raw);
                using var reader = info.ExecuteReader();
                bool sawTaskId = false;
                while (reader.Read())
                {
                    if (reader.GetString(1) == "task_id")
                    {
                        sawTaskId = true;
                        Assert.Equal(0, reader.GetInt32(3)); // notnull == 0
                    }
                }

                Assert.True(sawTaskId, "user_inbox lost its task_id column during the rebuild");

                using var idx = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='user_inbox'", raw);
                using var idxReader = idx.ExecuteReader();
                var indexes = new System.Collections.Generic.List<string>();
                while (idxReader.Read()) indexes.Add(idxReader.GetString(0));
                foreach (string expected in new[] { "idx_inbox_user", "idx_inbox_user_unread", "idx_inbox_task", "idx_inbox_created", "idx_inbox_type" })
                {
                    Assert.Contains(expected, indexes);
                }
            }

            // The legacy row survived the copy, with its task id intact.
            var legacy = Assert.Single(db.GetInboxMessages("Alice"), m => m.Id == "legacy01");
            Assert.Equal("task-123", legacy.TaskId);

            // And a taskless row now inserts alongside it.
            db.SaveInboxMessage(new InboxMessage
            {
                UserId = "Alice",
                TaskId = null,
                Type = "spawn_failed",
                Summary = "taskless after migration",
                CreatedBy = "MultiTerminal",
            });
            Assert.Equal(2, db.GetInboxMessages("Alice").Count(m => m.UserId == "Alice"));
        }

        [Fact]
        public void Migration_is_idempotent_on_an_already_nullable_table()
        {
            // On a fresh DB MigrateAddUserInbox now creates the nullable shape directly (pipeline
            // Run 4, debugger LOW), so MigrateUserInboxTaskIdNullable early-returns on first
            // construction; on a legacy DB it rebuilds once. Either way a second TaskDatabase over
            // the same file must not rebuild again or disturb the rows — the check is on PRAGMA
            // table_info, so an already-nullable table is left alone.
            string id;
            using (var first = new TaskDatabase())
            {
                var m = new InboxMessage { UserId = "Alice", TaskId = null, Type = "spawn_failed", Summary = "once", CreatedBy = "MultiTerminal" };
                first.SaveInboxMessage(m);
                id = m.Id;
            }

            SQLiteConnection.ClearAllPools();

            using var second = new TaskDatabase();
            Assert.Single(second.GetInboxMessages("Alice"), x => x.Id == id);
        }
    }
}
