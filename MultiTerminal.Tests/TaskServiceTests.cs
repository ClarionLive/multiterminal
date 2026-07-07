using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Unit tests for <see cref="TaskService"/> (ticket e7e89f4b) — the Kanban-task cache/CRUD/write-path
    /// extracted from MessageBroker. The POINT of the decomposition is that this can now be tested in
    /// isolation: a real temp-SQLite <see cref="TaskDatabase"/> + a stub <see cref="ITaskServiceHost"/>, with
    /// no MessageBroker, no REST server, no UI. The stub records event raises so we can assert the write path
    /// broadcasts; everything else it no-ops (task CRUD on non-worktree paths doesn't touch those
    /// collaborators). Proves the single write path (clone→persist→swap, from 1df2a534) survived the move.
    /// </summary>
    public sealed class TaskServiceTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly StubHost _host;
        private readonly TaskDatabase _db;
        private readonly TaskService _svc;

        public TaskServiceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_ts_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            _host = new StubHost();
            _db = new TaskDatabase();
            _svc = new TaskService(_db, _host);
        }

        public void Dispose()
        {
            _db.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void CreateTask_PersistsAndCaches_AndBroadcasts()
        {
            var result = _svc.CreateTask("title", "desc", "diana");
            Assert.True(result.Success);

            // cached
            var cached = _svc.GetTask(result.TaskId);
            Assert.NotNull(cached);
            Assert.Equal("title", cached.Title);
            // persisted (fresh service over the SAME db sees it after LoadPersistedTasks)
            using var freshDb = new TaskDatabase();
            var fresh = new TaskService(freshDb, new StubHost());
            fresh.LoadPersistedTasks();
            Assert.NotNull(fresh.GetTask(result.TaskId));
            // write path broadcast fired
            Assert.True(_host.TasksUpdatedCount >= 1);
        }

        [Fact]
        public void UpdateTaskStatus_MovesThroughWritePath_CacheAndDbCoherent()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            var r = _svc.UpdateTaskStatus(id, "in_progress");
            Assert.True(r.Success);
            Assert.Equal("in_progress", _svc.GetTask(id).Status);
            // clone→persist→swap keeps cache ≡ DB
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public void DeleteTask_RemovesFromCacheAndDb()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            Assert.NotNull(_svc.GetTask(id));

            var del = _svc.DeleteTask(id, "diana");
            Assert.True(del.Success);
            Assert.Null(_svc.GetTask(id));

            using var freshDb = new TaskDatabase();
            var fresh = new TaskService(freshDb, new StubHost());
            fresh.LoadPersistedTasks();
            Assert.Null(fresh.GetTask(id));  // gone from the DB too
        }

        [Fact]
        public void LoadPersistedTasks_PopulatesCacheFromDb()
        {
            var a = _svc.CreateTask("a", "d", "diana").TaskId;
            var b = _svc.CreateTask("b", "d", "diana").TaskId;

            // a brand-new service instance over the same DB starts empty until it loads
            using var freshDb = new TaskDatabase();
            var fresh = new TaskService(freshDb, new StubHost());
            Assert.Null(fresh.GetTask(a));
            fresh.LoadPersistedTasks();
            Assert.NotNull(fresh.GetTask(a));
            Assert.NotNull(fresh.GetTask(b));
        }

        [Fact]
        public void UpdateTaskPlan_Persists_AndCoherent()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            var r = _svc.UpdateTaskPlan(id, "## Plan\nstep 1", "diana");
            Assert.True(r.Success);
            Assert.Contains("step 1", _svc.GetTask(id).Plan);
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        // ── 7c59c004 atomicity capstone ──────────────────────────────────────────────────────────────

        [Fact]
        public async System.Threading.Tasks.Task PerTaskLock_SerializesConcurrentDifferentFieldWrites_NoLostUpdate()
        {
            // The field-level lost update (item 2): two writers concurrently mutate DIFFERENT fields of the
            // SAME task. Each write-path cycle clones the cached task and SaveTask persists the FULL row, so
            // without the per-task lock a stale clone's full-row write clobbers the other writer's field and
            // the later swap loses it. With the lock the read-modify-write serializes, so the last write of
            // EACH field survives. Hammered to make the interleave overwhelmingly likely on the old path.
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            const int N = 300;

            var w1 = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < N; i++) _svc.UpdateTaskPlan(id, "plan-" + i, "diana");
            });
            var w2 = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < N; i++) _svc.UpdateTaskContinuation(id, "notes-" + i, "diana");
            });
            await System.Threading.Tasks.Task.WhenAll(w1, w2);

            var final = _svc.GetTask(id);
            Assert.Equal("plan-" + (N - 1), final.Plan);                 // writer 1's field not clobbered
            Assert.Equal("notes-" + (N - 1), final.ContinuationNotes);   // writer 2's field not clobbered
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);          // cache ≡ DB throughout
        }

        [Fact]
        public void SetTaskActiveTransactional_RollsBackPause_WhenActivationTargetNotInProgress()
        {
            // Item 0: the pause+activate must be atomic. Seed an active task A for an assignee and a target B
            // that is NOT in_progress, then drive the DB transaction directly: the activation UPDATE (guarded
            // on status='in_progress') affects 0 rows and throws, so the sibling-pause of A must ROLL BACK.
            // Fails safe (A stays active) instead of open (A paused with nothing active).
            _db.SaveTask(new KanbanTask { Id = "A", Title = "A", Status = "in_progress", Assignee = "diana", SubStatus = "active", CreatedAt = DateTime.UtcNow });
            _db.SaveTask(new KanbanTask { Id = "B", Title = "B", Status = "todo", Assignee = "diana", SubStatus = null, CreatedAt = DateTime.UtcNow });

            Assert.Throws<InvalidOperationException>(() => _db.SetTaskActiveTransactional("B", "diana", DateTime.UtcNow));

            Assert.Equal("active", _db.GetTask("A").SubStatus);  // pause rolled back — A still active
            Assert.Equal("todo", _db.GetTask("B").Status);       // B untouched
        }

        [Fact]
        public void SetTaskActiveTransactional_PausesSiblingAndActivates_Atomically()
        {
            // Happy path: activating B atomically pauses the assignee's active sibling A and activates B.
            _db.SaveTask(new KanbanTask { Id = "A", Title = "A", Status = "in_progress", Assignee = "diana", SubStatus = "active", CreatedAt = DateTime.UtcNow });
            _db.SaveTask(new KanbanTask { Id = "B", Title = "B", Status = "in_progress", Assignee = "diana", SubStatus = "paused", CreatedAt = DateTime.UtcNow });

            var paused = _db.SetTaskActiveTransactional("B", "diana", DateTime.UtcNow);

            Assert.Contains("A", paused);
            Assert.Equal("active", _db.GetTask("B").SubStatus);
            Assert.Equal("paused", _db.GetTask("A").SubStatus);
        }

        [Fact]
        public async System.Threading.Tasks.Task SetTaskActive_ConcurrentSameAssignee_KeepsSingleActive_UnderBothLocks()
        {
            // F-B (7c59c004): two concurrent SetTaskActive calls for the SAME assignee contend on the
            // per-assignee activation lock (outermost) AND the per-task locks (WithTaskLocks) — both tiers
            // held together. The single-active-per-assignee invariant must hold under the storm: exactly one
            // of {a,b} active and the other paused, never two active, never a lost pause. Also a deadlock
            // probe — if the assignee/task lock ordering were invertible this would hang.
            var a = _svc.CreateTask("A", "d", "diana").TaskId;
            var b = _svc.CreateTask("B", "d", "diana").TaskId;
            _svc.ClaimTask(a, "diana", null);
            _svc.ClaimTask(b, "diana", null);
            _svc.UpdateTaskStatus(a, "in_progress");
            _svc.UpdateTaskStatus(b, "in_progress");

            const int N = 150;
            var t1 = System.Threading.Tasks.Task.Run(() => { for (int i = 0; i < N; i++) _svc.SetTaskActive(a, "diana"); });
            var t2 = System.Threading.Tasks.Task.Run(() => { for (int i = 0; i < N; i++) _svc.SetTaskActive(b, "diana"); });
            await System.Threading.Tasks.Task.WhenAll(t1, t2);

            var finalA = _db.GetTask(a);
            var finalB = _db.GetTask(b);
            int activeCount = (finalA.SubStatus == "active" ? 1 : 0) + (finalB.SubStatus == "active" ? 1 : 0);
            Assert.Equal(1, activeCount);                        // never two active, never zero
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);  // cache ≡ DB after the storm
        }

        [Fact]
        public async System.Threading.Tasks.Task UpdateTaskStatusDone_ConcurrentSetTaskActive_NeverTwoActive()
        {
            // New-1 (7c59c004 F-B completion): UpdateTaskStatus marks the active task done OUTSIDE the assignee
            // lock, then auto-resumes the most-recent paused task UNDER the lock. A concurrent SetTaskActive for
            // the same assignee, interleaving between those two steps, could (pre-fix) leave TWO active tasks.
            // The under-lock "assignee already has an active task? → skip resume" guard must keep it to ≤1.
            // Repeated to make the narrow window likely.
            for (int trial = 0; trial < 25; trial++)
            {
                var a = _svc.CreateTask($"A{trial}", "d", "diana").TaskId;
                var b = _svc.CreateTask($"B{trial}", "d", "diana").TaskId;
                var c = _svc.CreateTask($"C{trial}", "d", "diana").TaskId;
                foreach (var id in new[] { a, b, c })
                {
                    _svc.ClaimTask(id, "diana", null);
                    _svc.UpdateTaskStatus(id, "in_progress");
                }
                // a active; b most-recent paused; c older paused.
                _svc.SetTaskActive(c, "diana");
                _svc.SetTaskActive(b, "diana");
                _svc.SetTaskActive(a, "diana");

                // Race: mark the active task done (auto-resumes b) vs activate c.
                var t1 = System.Threading.Tasks.Task.Run(() => _svc.UpdateTaskStatus(a, "done"));
                var t2 = System.Threading.Tasks.Task.Run(() => _svc.SetTaskActive(c, "diana"));
                await System.Threading.Tasks.Task.WhenAll(t1, t2);

                int activeForDiana = _tasks_ActiveCount("diana");
                Assert.True(activeForDiana <= 1, $"trial {trial}: {activeForDiana} active tasks for diana (expected ≤1)");
            }
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        // Count DB rows that are active for an assignee (authoritative — asserts the invariant on the durable store).
        private int _tasks_ActiveCount(string assignee)
        {
            int n = 0;
            foreach (var t in _svc.GetTasks())
            {
                var fresh = _db.GetTask(t.Id);
                if (fresh != null && fresh.SubStatus == "active"
                    && string.Equals(fresh.Assignee, assignee, StringComparison.OrdinalIgnoreCase))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// Minimal <see cref="ITaskServiceHost"/> stub. Records the event raises (so the write path's
        /// broadcast is assertable); no-ops or returns benign defaults for the cross-region collaborators
        /// task CRUD doesn't exercise on the non-worktree paths these tests use.
        /// </summary>
        private sealed class StubHost : ITaskServiceHost
        {
            public int TasksUpdatedCount { get; private set; }

            public void RaiseTasksUpdated(List<KanbanTask> tasks) => TasksUpdatedCount++;
            public void RaiseTaskClaimed(TaskClaimedEventArgs args) { }
            public void RaiseTaskActiveChanged(TaskActiveChangedEventArgs args) { }
            public void LogError(string message) { }
            public void LogWarning(string message) { }
            public void LogInfo(string message) { }
            public void LogTrace(string message) { }
            public void RecordActivity(ActivityEvent activity, bool alreadyPersisted = false) { }
            public CreateInboxMessageResult CreateInboxNotification(string userId, string taskId, string taskTitle, int? checklistItemIndex, string checklistItemName, string type, string summary, string createdBy) => new CreateInboxMessageResult { Success = true };
            public void NotifyReportSaved(string taskId, string reportId, string agentName, string verdict) { }
            public Task<SendResult> NotifyHelperAdded(string helperName, string taskId, string taskTitle, string assignee) => Task.FromResult(new SendResult());
            public Task<SendResult> NotifyHelpRequested(string helperName, string taskId, string taskTitle, string requester, string details = null) => Task.FromResult(new SendResult());
            public string NormalizeProjectId(string raw) => raw;
            public string TryNormalizeProjectId(string raw, out bool ambiguous) { ambiguous = false; return raw; }
            public bool TryResolveWorktreeEligibility(KanbanTask task, out string projectPath, out string canonicalProjectId, out string skipReason)
            { projectPath = null; canonicalProjectId = null; skipReason = "test-stub: worktree off"; return false; }
            public bool TryGetProject(string projectId, out Project project) { project = null; return false; }
            public bool IsTemporaryAgent(string name) => name != null && name.StartsWith("Agent ", StringComparison.Ordinal);
            public WorktreeManager Worktrees => null;
            public WorktreeAutoCommitService AutoCommit => null;
            public WorktreeMergeService Merge => null;
            public object TaskWorktreeLock(string taskId) => _lock;
            public WorktreePruningEventArgs FireWorktreePruning(string taskId, string worktreePath, string repoRoot, string agentName) => null;
            public void PerformPostPruneMergeAndFireReady(string taskId, KanbanTask task, string projectPath, string worktreePath) { }
            public bool CommitAndIntegrateHelpers(KanbanTask task, string repoRoot, out List<string> integratedBranches) { integratedBranches = new List<string>(); return false; }
            public ActivityService ActivityService => null;
            public SummaryService SummaryService => null;
            public ComplexityDetector ComplexityDetector => null;
            public ChangelogService ChangelogService => null;
            public string DefaultInboxRecipient => "Owner";
            public void CleanupTaskAttachments(string taskId) { }

            private readonly object _lock = new object();
        }
    }
}
