using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Stale-task flags (ticket 28845a5f). The reminder for a paused task repeated because the level was
    /// written straight to the database while the broker's cached task kept level 0, so the next ordinary
    /// save of the task (SaveTask writes the whole row) reset it and the hourly check flagged it again.
    /// </summary>
    public sealed class StaleTaskFlagTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public StaleTaskFlagTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_stale_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_stale_msg_{stamp}.db");
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

        private long StoredLevel(string taskId)
        {
            using var c = new SQLiteConnection($"Data Source={_dbPath}");
            c.Open();
            using var cmd = new SQLiteCommand("SELECT stale_level FROM tasks WHERE id = @id", c);
            cmd.Parameters.AddWithValue("@id", taskId);
            var v = cmd.ExecuteScalar();
            return v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v);
        }

        [Fact]
        public void A_flag_survives_a_later_unrelated_save_of_the_task()
        {
            using var broker = new MessageBroker();
            var created = broker.CreateTask("paused work", "desc", "tester");
            Assert.True(created.Success, created.Error);
            string id = created.TaskId;

            Assert.True(broker.FlagTaskStale(id, 1));
            Assert.Equal(1, broker.GetTask(id).StaleLevel);

            // An ordinary save of the whole row, like the continuation-notes update that preceded the repeat.
            Assert.True(broker.RenameTask(id, "paused work, renamed", "tester").Success);

            Assert.Equal(1, StoredLevel(id));
        }

        [Fact]
        public void Still_relevant_clears_the_flag_in_the_cache_and_the_database()
        {
            using var broker = new MessageBroker();
            var created = broker.CreateTask("paused work", "desc", "tester");
            string id = created.TaskId;
            broker.FlagTaskStale(id, 1);

            var answer = broker.RespondToStale(id, "still_relevant", "tester");

            Assert.True(answer.Success, answer.Error);
            Assert.Equal(0, broker.GetTask(id).StaleLevel);
            Assert.Equal(0, StoredLevel(id));
        }

        [Fact]
        public void The_stale_check_writes_levels_through_its_hook_and_not_straight_to_the_database()
        {
            using var db = new TaskDatabase();
            db.SaveTask(new MultiTerminal.MCPServer.Models.KanbanTask
            {
                Id = "old1",
                Title = "paused for ten days",
                Status = "in_progress",
                SubStatus = "paused",
                PausedAt = DateTime.UtcNow.AddDays(-10),
                Assignee = "Alice",
            });
            var service = new StaleTaskService(db);
            var calls = new System.Collections.Generic.List<(string Id, int Level)>();
            service.ApplyLevel = (id, level) => { calls.Add((id, level)); return true; };

            var flagged = service.CheckAndFlagStaleTasks();

            Assert.Equal(new[] { ("old1", 1) }, calls);
            Assert.Single(flagged);
            Assert.Equal(0, StoredLevel("old1")); // the hook owns the write; nothing bypassed it
        }
        private static void SeedPausedTask(string id, int daysPaused)
        {
            using var db = new TaskDatabase();
            db.SaveTask(new MultiTerminal.MCPServer.Models.KanbanTask
            {
                Id = id,
                Title = "paused for a while",
                Status = "in_progress",
                SubStatus = "paused",
                PausedAt = DateTime.UtcNow.AddDays(-daysPaused),
                Assignee = "Alice",
            });
        }

        [Fact]
        public void Still_relevant_restarts_the_clock_so_the_next_check_does_not_remind_again()
        {
            // Pipeline Run 1 (Codex, Debugger): clearing the flag alone left paused_at 10 days old, so the very
            // next hourly check flagged the task again. The broker loads the seeded task into its cache.
            SeedPausedTask("old2", 10);
            using var broker = new MessageBroker();
            var stale = new StaleTaskService(broker.TaskDb) { ApplyLevel = broker.FlagTaskStale };

            Assert.Single(stale.CheckAndFlagStaleTasks());
            Assert.True(broker.RespondToStale("old2", "still_relevant", "tester").Success);

            Assert.Empty(stale.CheckAndFlagStaleTasks());
            Assert.Equal(0, StoredLevel("old2"));
            Assert.True(DateTime.UtcNow - broker.GetTask("old2").PausedAt.Value < TimeSpan.FromMinutes(1));
        }

        [Fact]
        public void A_stale_level_that_was_not_written_sends_no_reminder()
        {
            SeedPausedTask("old3", 10);
            using var db = new TaskDatabase();
            var failing = new StaleTaskService(db) { ApplyLevel = (_, _) => false };
            var throwing = new StaleTaskService(db) { ApplyLevel = (_, _) => throw new InvalidOperationException("disk") };

            Assert.Empty(failing.CheckAndFlagStaleTasks());
            Assert.Empty(throwing.CheckAndFlagStaleTasks());
        }
    }
}
