using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Failure-injection tests for ticket 1df2a534 (P5), pipeline Run 1 fixes A+B+C. The write-path
    /// mutators' CORE persist is FATAL: on a DB write failure they must return Success=false and leave the
    /// cache UNTOUCHED (coherent) — never a success-shaped result an agent would act on that evaporates on
    /// restart. Failure is injected with BEFORE INSERT/UPDATE/DELETE triggers on `tasks` that RAISE(ABORT):
    /// surgical, so every write to the tasks row throws while SELECT (GetTask / VerifyCacheCoherency) still
    /// works. Both DBs are isolated to temp files so the live app is never touched.
    /// </summary>
    public sealed class FailureInjectionCoherencyTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public FailureInjectionCoherencyTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_fi_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_fi_msg_{stamp}.db");
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
        public void UpdateTaskStatus_PersistFails_ReturnsFalse_CacheUnchanged_Coherent()
        {
            using var broker = new MessageBroker();
            var id = broker.CreateTask("t", "d", "tester").TaskId;
            Assert.True(broker.UpdateTaskStatus(id, "in_progress").Success);

            InjectWriteFailure();
            var result = broker.UpdateTaskStatus(id, "done");

            Assert.False(result.Success);                          // fatal, not swallowed
            Assert.NotEqual("done", broker.GetTask(id).Status);    // cache untouched
            Assert.True(broker.VerifyCacheCoherency(0).Coherent);  // cache still matches DB
        }

        [Fact]
        public void SetTaskActive_ActivationFails_ReturnsFalse_SiblingsUnpaused_Coherent()
        {
            using var broker = new MessageBroker();
            var t1 = broker.CreateTask("t1", "d", "alice").TaskId;
            var t2 = broker.CreateTask("t2", "d", "alice").TaskId;
            Assert.True(broker.UpdateTaskStatus(t1, "in_progress").Success);
            Assert.True(broker.SetTaskActive(t1, "alice").Success);       // t1 active
            Assert.True(broker.UpdateTaskStatus(t2, "in_progress").Success);
            Assert.Equal("active", broker.GetTask(t1).SubStatus);

            InjectWriteFailure();
            var result = broker.SetTaskActive(t2, "alice");

            Assert.False(result.Success);
            Assert.Equal("active", broker.GetTask(t1).SubStatus);    // sibling NOT paused (activate-first ruling)
            Assert.NotEqual("active", broker.GetTask(t2).SubStatus); // t2 not activated
            Assert.True(broker.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public void ClaimTask_PersistFails_ReturnsFalse_NotClaimed_Coherent()
        {
            using var broker = new MessageBroker();
            var id = broker.CreateTask("t", "d", null).TaskId;   // unclaimed todo task

            InjectWriteFailure();
            var result = broker.ClaimTask(id, "worker");

            Assert.False(result.Success);
            Assert.NotEqual("active", broker.GetTask(id).SubStatus);  // never became active
            Assert.True(broker.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public void ReorderTask_SortPersistFails_ReturnsFalse_CacheUnchanged_Coherent()
        {
            using var broker = new MessageBroker();
            var id = broker.CreateTask("t", "d", "tester").TaskId;
            var beforeSort = broker.GetTask(id).SortOrder;

            InjectWriteFailure();
            var result = broker.ReorderTask(id, null, 987654.0, "tester");

            Assert.False(result.Success);
            Assert.Equal(beforeSort, broker.GetTask(id).SortOrder);  // cache untouched (persist-first)
            Assert.True(broker.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public void VerifyCacheCoherency_DetectsOmittedFieldDivergence_StaleResponse()
        {
            using var broker = new MessageBroker();
            var id = broker.CreateTask("t", "d", "tester").TaskId;
            Assert.True(broker.VerifyCacheCoherency(0).Coherent);

            // Force a divergence on stale_response — a field previously OMITTED from the coherency
            // comparison (P5 pipeline C expanded the set to catch exactly this).
            using (var c = OpenRaw())
            {
                using var cmd = new SQLiteCommand("UPDATE tasks SET stale_response = 'diverged' WHERE id = @id", c);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }

            var report = broker.VerifyCacheCoherency(0);
            Assert.False(report.Coherent, "an omitted-then-added field divergence must be detected");
            Assert.Contains(report.Divergences, d => d.Contains(id));
        }

        [Fact]
        public void GetMyActiveTask_TwoActiveRows_ResolvesDeterministically_NewestUpdatedAtWins()
        {
            using var broker = new MessageBroker();   // schema created, cache empty
            // Seed TWO active rows for one agent directly in the DB (the sibling-pause-failed two-active
            // state that P5 pipeline Run 2 accepts as a documented gap), with distinct updated_at. The
            // cache is empty (rows inserted post-load) so GetMyActiveTask resolves via the DB query, which
            // now ORDERs BY updated_at DESC — deterministic, newest activation wins.
            InsertActiveRow("older", "alice", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            InsertActiveRow("newer", "alice", new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var a = broker.GetMyActiveTask("alice");
            var b = broker.GetMyActiveTask("alice");

            Assert.NotNull(a);
            Assert.Equal(a.Id, b.Id);     // deterministic across calls (was a nondeterministic LIMIT 1)
            Assert.Equal("newer", a.Id);  // newest updated_at wins
        }

        private void InsertActiveRow(string id, string assignee, DateTime updatedAt)
        {
            using var c = OpenRaw();
            using var cmd = new SQLiteCommand(
                "INSERT INTO tasks (id, title, status, assignee, sub_status, created_at, updated_at) " +
                "VALUES (@id, @title, 'in_progress', @assignee, 'active', @created, @updated)", c);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@title", "task " + id);
            cmd.Parameters.AddWithValue("@assignee", assignee);
            cmd.Parameters.AddWithValue("@created", updatedAt);
            cmd.Parameters.AddWithValue("@updated", updatedAt);
            cmd.ExecuteNonQuery();
        }

        private void InjectWriteFailure()
        {
            using var c = OpenRaw();
            foreach (var op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                // op is a hardcoded test literal (never user input) — RAISE(ABORT) on every tasks write.
#pragma warning disable CA2100
                using var cmd = new SQLiteCommand(
                    $"CREATE TRIGGER inject_fail_{op} BEFORE {op} ON tasks BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;", c);
#pragma warning restore CA2100
                cmd.ExecuteNonQuery();
            }
        }

        private SQLiteConnection OpenRaw()
        {
            var c = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = _dbPath, Version = 3 }.ToString());
            c.Open();
            return c;
        }
    }
}
