using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Empirical thread-safety smoke for TaskDatabase's <c>_dbLock</c>/<c>LockConn()</c> gate
    /// (task ad08caac). Hammers TaskDatabase's own CRUD from many threads at once and asserts
    /// no exception and no corrupted row count — the runtime complement to the static gate
    /// verifier (scripts/verify-taskdb-gate.mjs).
    ///
    /// SCOPE: this proves TaskDatabase-INTERNAL concurrency — that TaskDatabase's own methods,
    /// all routed through LockConn(), don't corrupt its connection under concurrent calls. As of
    /// ticket bb2b0104 the sibling classes (KnowledgeDatabase, CodeGraphDatabase,
    /// SessionMemoryDatabase, BranchMetadataService, OwnerProfileService, SourceControlAccountService)
    /// each own their OWN connection instead of borrowing this one, so the old cross-class race on a
    /// shared handle is gone; <see cref="CrossConnectionConcurrencyTests"/> exercises those separate
    /// connections running concurrently.
    /// </summary>
    public sealed class TaskDatabaseInternalConcurrencyTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly TaskDatabase _db;

        public TaskDatabaseInternalConcurrencyTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_conc_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _db = new TaskDatabase();
        }

        public void Dispose()
        {
            _db?.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void HammerCrud_ConcurrentInternalAccess_NoThrowNoCorruption()
        {
            const int workers = 16;
            const int opsPerWorker = 60;

            // Seed one long-lived row per worker so each worker has a stable row to UPDATE
            // concurrently (writer/writer + writer/reader contention on the same connection).
            var seedIds = new string[workers];
            for (int w = 0; w < workers; w++)
            {
                var seed = new KanbanTask { Title = $"seed-{w}", Status = "todo" };
                _db.SaveTask(seed);
                seedIds[w] = seed.Id;
            }

            var failures = new ConcurrentQueue<Exception>();
            var createdIds = new ConcurrentBag<string>();

            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
            {
                try
                {
                    for (int op = 0; op < opsPerWorker; op++)
                    {
                        // INSERT (writer)
                        var t = new KanbanTask { Title = $"w{w}-op{op}", Status = "todo" };
                        _db.SaveTask(t);
                        createdIds.Add(t.Id);

                        // full-table read (reader) — a live SQLiteDataReader mid-iteration is
                        // exactly what an overlapping command would corrupt without the gate
                        var all = _db.LoadAllTasks();
                        Assert.NotNull(all);

                        // UPDATE the shared seed row (writer contending across all workers)
                        _db.UpdateTask(seedIds[w], $"upd-{op}", $"desc-{w}-{op}");

                        // point read of the just-inserted row (reader)
                        var got = _db.GetTask(t.Id);
                        if (got == null)
                        {
                            throw new InvalidOperationException($"just-saved task {t.Id} not found");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });

            Assert.True(failures.IsEmpty, $"Concurrent access threw {failures.Count} exception(s); first: {failures.FirstOrDefault()}");

            // No lost/duplicated rows: seeds + all created rows are present exactly once.
            var final = _db.LoadAllTasks();
            Assert.Equal(workers + (workers * opsPerWorker), final.Count);
            Assert.Equal(final.Count, final.Select(t => t.Id).Distinct().Count());
            foreach (var id in seedIds)
            {
                Assert.NotNull(_db.GetTask(id));
            }
        }
    }
}
