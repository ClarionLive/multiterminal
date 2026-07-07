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
