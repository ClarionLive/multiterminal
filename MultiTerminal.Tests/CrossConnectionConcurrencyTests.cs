using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Cross-CONNECTION concurrency hammer for ticket bb2b0104. Where
    /// <see cref="TaskDatabaseInternalConcurrencyTests"/> proves ONE class serializes its OWN
    /// connection, this proves the invariant bb2b0104 established: several owner classes, each
    /// holding its OWN connection to the same multiterminal.db file, run concurrently without
    /// corruption — WAL gives readers a consistent snapshot and serializes writers at commit,
    /// with busy_timeout absorbing writer/writer contention invisibly.
    ///
    /// The workload is exactly the race Alice named: concurrent task CRUD + chat writes (on
    /// TaskDatabase's connection) + knowledge writes/reads (KnowledgeDatabase's connection) +
    /// a heavy code-graph "reindex" transaction (CodeGraphDatabase's connection) all at once,
    /// against a real on-disk WAL database. Pre-bb2b0104 these ran on ONE borrowed handle and
    /// raced; now each is a separate owned connection.
    ///
    /// Asserts: no exceptions (busy_timeout retries stay invisible), and exact row counts with
    /// no lost or duplicated rows across every table touched.
    /// </summary>
    public sealed class CrossConnectionConcurrencyTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly TaskDatabase _taskDb;
        private readonly KnowledgeDatabase _knowledgeDb;
        private readonly CodeGraphDatabase _codeGraphDb;
        private readonly PlanDatabase _planDb;
        private readonly ActivityFeedService _activityDb;

        public CrossConnectionConcurrencyTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_xconn_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);

            // Each of these opens its OWN connection to the SAME test DB via MultiterminalDb.Open()
            // (honors MULTITERMINAL_TEST_DB). TaskDatabase creates the schema they rely on.
            // _planDb and _activityDb are the two census stragglers conformed in bb2b0104 (the 9th/10th
            // owners) — included here so the concurrent workload proves them WAL-safe alongside the rest.
            _taskDb = new TaskDatabase();
            _knowledgeDb = new KnowledgeDatabase(_taskDb);   // reads IsFts5Available (a bool), owns its own connection
            _codeGraphDb = new CodeGraphDatabase();          // owns its own connection
            _planDb = new PlanDatabase();                    // owns its own connection (census straggler #1)
            _activityDb = new ActivityFeedService();         // owns its own connection (census straggler #2)
        }

        public void Dispose()
        {
            _activityDb?.Dispose();
            _planDb?.Dispose();
            _codeGraphDb?.Dispose();
            _knowledgeDb?.Dispose();
            _taskDb?.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
            // Also drop the -wal / -shm sidecars WAL leaves next to the db file.
            foreach (var side in new[] { _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(side)) File.Delete(side);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void HammerMixedWorkload_ConcurrentCrossConnection_NoThrowNoCorruption()
        {
            const int workers = 12;
            const int opsPerWorker = 40;
            const int reindexSymbols = 500; // the "heavy reindex" transaction size

            // One code-graph project row every symbol references.
            int projectId = _codeGraphDb.InsertProject("HammerProj", "Hammer.csproj");

            var failures = new ConcurrentQueue<Exception>();
            var taskIds = new ConcurrentBag<string>();
            var knowledgeIds = new ConcurrentBag<int>();
            var symbolIds = new ConcurrentBag<long>();

            // Worker 0 is the "reindexer": it holds CodeGraphDatabase's connection in one big
            // write transaction (mimicking the 2-pass Roslyn reindex) while every other worker
            // hammers the OTHER connections. Under WAL this must not block their reads, and the
            // writer contention at commit must be absorbed by busy_timeout — no SQLITE_BUSY.
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
            {
                try
                {
                    if (w == 0)
                    {
                        using var txn = _codeGraphDb.BeginTransaction();
                        try
                        {
                            for (int i = 0; i < reindexSymbols; i++)
                            {
                                long id = _codeGraphDb.InsertSymbol(new CodeSymbol
                                {
                                    Name = $"ReindexSym{i}",
                                    Type = "method",
                                    FilePath = "Reindex.cs",
                                    LineNumber = i,
                                    ProjectId = projectId
                                });
                                symbolIds.Add(id);
                            }
                            txn.Commit();
                        }
                        finally { _codeGraphDb.EndTransaction(); }
                        return;
                    }

                    for (int op = 0; op < opsPerWorker; op++)
                    {
                        // --- TaskDatabase connection: writer + full-table reader + chat writer ---
                        var t = new KanbanTask { Title = $"w{w}-op{op}", Status = "todo" };
                        _taskDb.SaveTask(t);
                        taskIds.Add(t.Id);

                        var all = _taskDb.LoadAllTasks(); // reader mid-iteration under cross-connection writes
                        Assert.NotNull(all);

                        _taskDb.SaveChatMessage(
                            Guid.NewGuid().ToString("N"), $"w{w}", "all", $"msg-{w}-{op}", DateTime.UtcNow, true);

                        // --- KnowledgeDatabase connection: writer + read-back ---
                        int kid = _knowledgeDb.AddKnowledgeEntry(new KnowledgeEntry
                        {
                            Title = $"k-{w}-{op}",
                            Content = $"content-{w}-{op}",
                            Category = "general"
                        });
                        knowledgeIds.Add(kid);
                        Assert.NotNull(_knowledgeDb.GetKnowledgeEntry(kid));

                        // --- CodeGraphDatabase connection: writer + reader, concurrent with the reindex txn ---
                        long sid = _codeGraphDb.InsertSymbol(new CodeSymbol
                        {
                            Name = $"Sym-{w}-{op}",
                            Type = "method",
                            FilePath = $"W{w}.cs",
                            LineNumber = op,
                            ProjectId = projectId
                        });
                        symbolIds.Add(sid);
                        Assert.NotNull(_codeGraphDb.LoadSymbolLookup()); // reader while symbols are being written

                        // --- PlanDatabase connection (census straggler #1): writer + read-back ---
                        // status "draft" avoids DeactivateAllPlans so counts stay deterministic.
                        _planDb.SavePlan(new Plan
                        {
                            Id = $"plan-{w}-{op}",
                            Title = $"p{w}-{op}",
                            Status = "draft",
                            CurrentPhase = "design",
                            CreatedAt = DateTime.UtcNow
                        });

                        // --- ActivityFeedService connection (census straggler #2, 10th owner): writer ---
                        // activity_feed is written ONLY by ActivityFeedService; the cross-connection
                        // contention here is its connection vs the task/knowledge/code-graph connections
                        // on the same WAL file (the race bb2b0104 closes for this owner too).
                        _activityDb.RecordGeneralActivity("XCONN", $"w{w}", $"act-{w}-{op}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });

            Assert.True(failures.IsEmpty,
                $"Cross-connection workload threw {failures.Count} exception(s); first: {failures.FirstOrDefault()}");

            // Exact row counts, no lost/duplicated rows on any connection:
            int nonReindexWorkers = workers - 1;

            // tasks — one per (worker>0, op)
            var finalTasks = _taskDb.LoadAllTasks();
            Assert.Equal(nonReindexWorkers * opsPerWorker, finalTasks.Count);
            Assert.Equal(finalTasks.Count, finalTasks.Select(t => t.Id).Distinct().Count());
            Assert.Equal(nonReindexWorkers * opsPerWorker, taskIds.Distinct().Count());

            // knowledge_entries — auto-increment ids all distinct, count exact (gate serialized them)
            Assert.Equal(nonReindexWorkers * opsPerWorker, knowledgeIds.Count);
            Assert.Equal(knowledgeIds.Count, knowledgeIds.Distinct().Count());

            // cg_symbols — reindex batch + per-op inserts, all ids distinct, count exact
            int expectedSymbols = reindexSymbols + (nonReindexWorkers * opsPerWorker);
            Assert.Equal(expectedSymbols, symbolIds.Count);
            Assert.Equal(symbolIds.Count, symbolIds.Distinct().Count());
            Assert.Equal(expectedSymbols, _codeGraphDb.LoadSymbolLookup().Count);

            // plans (census straggler #1) — one draft plan per (worker>0, op), all distinct ids, exact count
            var finalPlans = _planDb.GetAllPlans();
            Assert.Equal(nonReindexWorkers * opsPerWorker, finalPlans.Count);
            Assert.Equal(finalPlans.Count, finalPlans.Select(p => p.Id).Distinct().Count());

            // activity_feed (census straggler #2, 10th owner) — one "XCONN" row per (worker>0, op), exact count
            var xconnActivities = _activityDb.GetActivitiesByType("XCONN", int.MaxValue);
            Assert.Equal(nonReindexWorkers * opsPerWorker, xconnActivities.Count);
        }
    }
}
