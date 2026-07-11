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

            Assert.Throws<InvalidOperationException>(() => _db.SetTaskActiveTransactional("B", new List<string> { "A" }, DateTime.UtcNow));

            Assert.Equal("active", _db.GetTask("A").SubStatus);  // pause rolled back — A still active
            Assert.Equal("todo", _db.GetTask("B").Status);       // B untouched
        }

        [Fact]
        public void SetTaskActiveTransactional_PausesSiblingAndActivates_Atomically()
        {
            // Happy path: activating B atomically pauses the assignee's active sibling A and activates B.
            _db.SaveTask(new KanbanTask { Id = "A", Title = "A", Status = "in_progress", Assignee = "diana", SubStatus = "active", CreatedAt = DateTime.UtcNow });
            _db.SaveTask(new KanbanTask { Id = "B", Title = "B", Status = "in_progress", Assignee = "diana", SubStatus = "paused", CreatedAt = DateTime.UtcNow });

            var paused = _db.SetTaskActiveTransactional("B", new List<string> { "A" }, DateTime.UtcNow);

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

                int activeForDiana = DbActiveCount("diana");
                Assert.True(activeForDiana <= 1, $"trial {trial}: {activeForDiana} active tasks for diana (expected ≤1)");
            }
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public async System.Threading.Tasks.Task ClaimTask_ConcurrentSetTaskActive_KeepsSingleActive()
        {
            // 7c59c004 Codex class-close: ClaimTask's activation now routes through the SAME ActivateExclusively
            // primitive (under the per-assignee lock) as SetTaskActive, so a concurrent claim-activate + activate
            // for one assignee can't leave a durable two-active (the off-lock MakeTaskActive race Codex flagged).
            for (int trial = 0; trial < 20; trial++)
            {
                var a = _svc.CreateTask($"A{trial}", "d", "diana").TaskId;
                var b = _svc.CreateTask($"B{trial}", "d", "diana").TaskId;
                var c = _svc.CreateTask($"C{trial}", "d", "diana").TaskId;   // stays todo until claimed
                _svc.ClaimTask(a, "diana", null);
                _svc.ClaimTask(b, "diana", null);
                _svc.UpdateTaskStatus(a, "in_progress");
                _svc.UpdateTaskStatus(b, "in_progress");
                _svc.SetTaskActive(a, "diana");   // A active; B in_progress; C todo

                // Race: activate B (already claimed) vs claim+activate C (urgent → MakeTaskActive path).
                var t1 = System.Threading.Tasks.Task.Run(() => _svc.SetTaskActive(b, "diana"));
                var t2 = System.Threading.Tasks.Task.Run(() => _svc.ClaimTask(c, "diana", "urgent"));
                await System.Threading.Tasks.Task.WhenAll(t1, t2);

                Assert.True(DbActiveCount("diana") <= 1, $"trial {trial}: >1 active for diana (durable two-active)");
            }
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public async System.Threading.Tasks.Task UrgentClaim_ConcurrentReactivateOldActive_NeverZeroActive()
        {
            // 7c59c004 Codex CONFIRMATION-round finding: the urgent-claim path used to re-pause the pre-lock
            // active task via PauseTaskWithSummary AFTER MakeTaskActive released the assignee lock. If a
            // serialized SetTaskActive re-activated that same task in the window, the stale off-lock re-pause
            // clobbered it → ZERO durable active for the assignee (a lost activation — the DUAL of two-active,
            // which the ≤1 tests above don't catch). The fix removes that state write: ActivateExclusively is
            // the SOLE make-active authority and already paused the sibling atomically; the caller only emits
            // summary/activity keyed off the returned paused set. So EXACTLY ONE active must survive every
            // interleaving — never zero (the regression this guards), never two.
            for (int trial = 0; trial < 40; trial++)
            {
                var a = _svc.CreateTask($"A{trial}", "d", "diana").TaskId;
                var c = _svc.CreateTask($"C{trial}", "d", "diana").TaskId;   // stays todo until the urgent claim
                _svc.ClaimTask(a, "diana", null);
                _svc.UpdateTaskStatus(a, "in_progress");
                _svc.SetTaskActive(a, "diana");   // A active; C todo (prior trials' tasks all paused by single-active)

                // Race: urgent claim of C (pauses A + activates C atomically under the lock, then emits the pause
                // summary off-lock) vs SetTaskActive(A) (re-activates A). Pre-fix the stale off-lock re-pause of A
                // could land AFTER the re-activation → zero active. Post-fix: always exactly one.
                var t1 = System.Threading.Tasks.Task.Run(() => _svc.ClaimTask(c, "diana", "urgent"));
                var t2 = System.Threading.Tasks.Task.Run(() => _svc.SetTaskActive(a, "diana"));
                await System.Threading.Tasks.Task.WhenAll(t1, t2);

                int active = DbActiveCount("diana");
                Assert.Equal(1, active);   // EXACTLY one — never zero (stale-pause regression), never two
            }
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public void UrgentClaim_ThrowingRecordActivitySink_DoesNotPoisonCommittedClaim()
        {
            // 7c59c004 Codex security [medium]: RecordActivity is a POST-COMMIT best-effort sink in the make-active
            // path (both MakeTaskActive and EmitPauseSummaries). A throwing sink must NEVER escape and turn a
            // committed claim + exclusive activation into a reported failure (the RaiseSafe resilient-dispatch
            // principle from 1df2a534, applied to the activity sink). Setup: A active, C todo.
            var a = _svc.CreateTask("A", "d", "diana").TaskId;
            var c = _svc.CreateTask("C", "d", "diana").TaskId;
            _svc.ClaimTask(a, "diana", null);
            _svc.UpdateTaskStatus(a, "in_progress");
            _svc.SetTaskActive(a, "diana");   // A active; C todo

            // Make the activity sink throw, then urgently claim C: MakeTaskActive commits (pauses A + activates C)
            // and EmitPauseSummaries runs — both hit the throwing RecordActivity, now exception-contained.
            _host.ThrowFromRecordActivity = true;
            var result = _svc.ClaimTask(c, "diana", "urgent");
            _host.ThrowFromRecordActivity = false;

            Assert.True(result.Success, "committed urgent claim must report success even when the activity sink throws");
            Assert.Equal(1, DbActiveCount("diana"));           // exactly one active — the committed activation stands
            Assert.Equal("active", _db.GetTask(c).SubStatus);  // C active (the urgent claim)
            Assert.Equal("paused", _db.GetTask(a).SubStatus);  // A paused by ActivateExclusively's atomic txn
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        // Count DB rows that are active for an assignee (authoritative — asserts the invariant on the durable store).
        private int DbActiveCount(string assignee)
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

        [Theory]
        [InlineData("Diana", "diana")]    // ASCII case variant
        [InlineData("Élodie", "élodie")]  // NON-ASCII case variant — SQLite COLLATE NOCASE would MISS this row
        public void SetTaskActive_CaseVariantAssignee_KeepsSingleActive(string activeCase, string activatingCase)
        {
            // 7c59c004 Codex class-close: SetTaskActive discovers siblings with C# OrdinalIgnoreCase and pauses
            // them BY ID (no assignee SQL collation), so a case-variant active sibling — INCLUDING non-ASCII,
            // where SQLite COLLATE NOCASE folds nothing and would leave a durable two-active — is still paused.
            var a = _svc.CreateTask("A", "d", activeCase).TaskId;
            var b = _svc.CreateTask("B", "d", activatingCase).TaskId;
            _svc.ClaimTask(a, activeCase, null);
            _svc.ClaimTask(b, activatingCase, null);
            _svc.UpdateTaskStatus(a, "in_progress");
            _svc.UpdateTaskStatus(b, "in_progress");
            _svc.SetTaskActive(a, activeCase);       // A active as the differently-cased assignee

            _svc.SetTaskActive(b, activatingCase);   // activate B under the case-variant name

            Assert.Equal("paused", _db.GetTask(a).SubStatus);   // A paused despite the case (incl. non-ASCII)
            Assert.Equal("active", _db.GetTask(b).SubStatus);   // only B active — no durable two-active
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        // ── cf32b08f reassign-on-claim ────────────────────────────────────────────────────────────────

        [Fact]
        public void ClaimTask_ClaimedByOther_BlocksWithoutReassign()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            Assert.True(_svc.ClaimTask(id, "diana", null).Success);

            var result = _svc.ClaimTask(id, "bob", null);

            Assert.False(result.Success);
            Assert.Contains("diana", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("diana", _db.GetTask(id).Assignee);   // assignee untouched
        }

        [Fact]
        public void ClaimTask_AllowReassign_TakesOverAndRecordsAudit()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            Assert.True(_svc.ClaimTask(id, "diana", null).Success);

            var result = _svc.ClaimTask(id, "bob", null, allowReassign: true);

            Assert.True(result.Success);
            Assert.Equal("bob", _db.GetTask(id).Assignee);     // durably reassigned
            Assert.Contains(_host.Activities, a => a.Action == "reassigned"
                && a.Content.Contains("diana") && a.Content.Contains("bob"));
        }

        [Fact]
        public void ClaimTask_AllowReassign_SamePerson_NoReassignAudit()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            Assert.True(_svc.ClaimTask(id, "diana", null).Success);

            // Re-claim by the same person (case-variant) with the flag set: succeeds, but it is
            // NOT a reassignment, so no "reassigned" audit event may be emitted.
            var result = _svc.ClaimTask(id, "Diana", null, allowReassign: true);

            Assert.True(result.Success);
            Assert.DoesNotContain(_host.Activities, a => a.Action == "reassigned");
        }

        [Fact]
        public void ClaimTask_UnassignedCrossProject_Refused()
        {
            // HIGH-3 (cf32b08f security, Run 2): the claim-scope binding must cover the UNASSIGNED
            // path too, not just takeovers — a scoped caller must not claim another project's
            // unassigned task by id. Unbound (null expected) and explicit-global claims still work.
            var id = _svc.CreateTask("t", "d", "diana", projectId: "projB123").TaskId;

            var refused = _svc.ClaimTask(id, "bob", null, allowReassign: false, expectedProjectId: "projA456");

            Assert.False(refused.Success);
            Assert.Contains("projB123", refused.Error);
            Assert.Null(_db.GetTask(id).Assignee);   // still unassigned

            var allowed = _svc.ClaimTask(id, "bob", null);   // no binding = legacy/global mode
            Assert.True(allowed.Success);
            Assert.Equal("bob", _db.GetTask(id).Assignee);
        }

        [Fact]
        public void ClaimTask_Reassign_CrossProject_Refused()
        {
            // HIGH-2 (cf32b08f adversary): the takeover write is bound to the caller's project scope.
            // A reassign carrying expectedProjectId for project A must be refused for a project-B task,
            // leaving the assignment untouched; the same call without the binding (global mode) succeeds.
            var id = _svc.CreateTask("t", "d", "diana", projectId: "projB123").TaskId;
            Assert.True(_svc.ClaimTask(id, "diana", null).Success);

            var refused = _svc.ClaimTask(id, "bob", null, allowReassign: true, expectedProjectId: "projA456");

            Assert.False(refused.Success);
            Assert.Contains("projB123", refused.Error);
            Assert.Equal("diana", _db.GetTask(id).Assignee);   // untouched
            Assert.DoesNotContain(_host.Activities, a => a.Action == "reassigned");

            // Matching project binding succeeds (case-insensitive), as does explicit global mode (null).
            var allowed = _svc.ClaimTask(id, "bob", null, allowReassign: true, expectedProjectId: "PROJB123");
            Assert.True(allowed.Success);
            Assert.Equal("bob", _db.GetTask(id).Assignee);
        }

        [Fact]
        public void ClaimTask_Reassign_OtherAgentsActiveTask_MovesToNewAssignee()
        {
            // Steal diana's ACTIVE task: after the reassign, diana must have zero active tasks and
            // bob (no prior active work) must hold the task as HIS active task.
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            _svc.ClaimTask(id, "diana", null);
            _svc.UpdateTaskStatus(id, "in_progress");
            _svc.SetTaskActive(id, "diana");
            Assert.Equal(1, DbActiveCount("diana"));

            var result = _svc.ClaimTask(id, "bob", null, allowReassign: true);

            Assert.True(result.Success);
            Assert.Equal(0, DbActiveCount("diana"));
            Assert.Equal(1, DbActiveCount("bob"));
            Assert.Equal("bob", _db.GetTask(id).Assignee);
            Assert.True(_svc.VerifyCacheCoherency(0).Coherent);
        }

        [Fact]
        public void GetActiveTaskForAgent_MatchesCaseVariantAssignee()
        {
            // Same root (sibling fix): the active-task resolution must find the row regardless of casing.
            _db.SaveTask(new KanbanTask { Id = "A", Title = "A", Status = "in_progress", Assignee = "Diana", SubStatus = "active", CreatedAt = DateTime.UtcNow });
            var active = _db.GetActiveTaskForAgent("diana");
            Assert.NotNull(active);
            Assert.Equal("A", active.Id);
        }

        /// <summary>
        /// Minimal <see cref="ITaskServiceHost"/> stub. Records the event raises (so the write path's
        /// broadcast is assertable); no-ops or returns benign defaults for the cross-region collaborators
        /// task CRUD doesn't exercise on the non-worktree paths these tests use.
        /// </summary>
        private sealed class StubHost : ITaskServiceHost
        {
            public int TasksUpdatedCount { get; private set; }

            // Captured RecordActivity events, so audit-trail emissions (e.g. cf32b08f "reassigned") are assertable.
            public List<ActivityEvent> Activities { get; } = new List<ActivityEvent>();

            // When set, RecordActivity throws — models a post-commit best-effort activity sink going down, to
            // prove a throwing sink can't poison a committed claim (7c59c004 Codex security [medium]).
            public bool ThrowFromRecordActivity { get; set; }

            public void RaiseTasksUpdated(List<KanbanTask> tasks) => TasksUpdatedCount++;
            public void RaiseTaskClaimed(TaskClaimedEventArgs args) { }
            public void RaiseTaskActiveChanged(TaskActiveChangedEventArgs args) { }
            public void LogError(string message) { }
            public void LogWarning(string message) { }
            public void LogInfo(string message) { }
            public void LogTrace(string message) { }
            public bool RecordActivity(ActivityEvent activity, bool alreadyPersisted = false)
            {
                if (ThrowFromRecordActivity) throw new InvalidOperationException("test: activity sink down");
                Activities.Add(activity);
                return true;
            }
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
