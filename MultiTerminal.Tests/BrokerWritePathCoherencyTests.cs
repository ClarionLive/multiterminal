using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Broker write-path cache-coherency tests for ticket 1df2a534 (P5), items 1 + 4. Constructs a REAL
    /// MessageBroker — both its databases isolated to temp files (MULTITERMINAL_TEST_DB for tasks/projects/
    /// profiles; MULTITERMINAL_TEST_MSGDB for the message queue, now honored by the ctor) so the test never
    /// touches the live app's data — drives task mutations through the single write path, and asserts
    /// <see cref="MessageBroker.VerifyCacheCoherency"/> finds the <c>_tasks</c> cache in lockstep with the
    /// tasks table.
    ///
    /// The negative control forces a cache/DB divergence directly in the DB and asserts the check DETECTS
    /// it — so a green positive result can't be vacuous (the same falsifiability discipline as the static
    /// verifiers' --self-test).
    /// </summary>
    public sealed class BrokerWritePathCoherencyTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;

        public BrokerWritePathCoherencyTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_coh_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_coh_msg_{stamp}.db");
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
        public void WritePath_KeepsCacheCoherentWithDb()
        {
            using var broker = new MessageBroker();

            var ids = new List<string>();
            for (int i = 0; i < 15; i++)
            {
                var r = broker.CreateTask($"task {i}", $"desc {i}", "tester");
                Assert.True(r.Success, r.Error);
                ids.Add(r.TaskId);
            }

            // Drive a representative mix of write-path mutators (single-field, activity-recording,
            // claim/activate, and status change with its auto-resume branch).
            foreach (var id in ids)
            {
                broker.UpdateTaskPriority(id, "urgent");
                broker.UpdateTaskPlan(id, $"plan for {id}", "tester");
                broker.UpdateTaskContinuation(id, "handoff notes", "tester");
                broker.ClaimTask(id, "worker");
                broker.UpdateTaskStatus(id, "in_progress");
            }

            var report = broker.VerifyCacheCoherency(0); // 0 = check every cached task
            Assert.True(report.Coherent, $"cache/DB divergence: {string.Join(" | ", report.Divergences)}");
            Assert.Equal(ids.Count, report.CachedCount);
            Assert.Equal(ids.Count, report.Checked);
        }

        [Fact]
        public void VerifyCacheCoherency_DetectsForcedDivergence()
        {
            using var broker = new MessageBroker();
            var r = broker.CreateTask("t", "d", "tester");
            Assert.True(r.Success, r.Error);

            // Coherent immediately after a write-path create.
            Assert.True(broker.VerifyCacheCoherency(0).Coherent);

            // Force a divergence: mutate the tasks row directly, behind the broker's back. WAL makes the
            // committed change visible to the broker's next read, but its cache still holds the old status.
            using (var c = OpenRaw(_dbPath))
            {
                using var cmd = new SQLiteCommand("UPDATE tasks SET status = 'done' WHERE id = @id", c);
                cmd.Parameters.AddWithValue("@id", r.TaskId);
                cmd.ExecuteNonQuery();
            }

            var report = broker.VerifyCacheCoherency(0);
            Assert.False(report.Coherent, "a direct DB mutation behind the cache must be detected");
            Assert.Contains(report.Divergences, d => d.Contains(r.TaskId));
        }

        private static SQLiteConnection OpenRaw(string dbPath)
        {
            var c = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = dbPath, Version = 3 }.ToString());
            c.Open();
            return c;
        }
    }
}
