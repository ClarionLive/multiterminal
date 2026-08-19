using System;
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

        /// <summary>
        /// Regression for task 60665c6c. AppendChecklistItems rebuilds each item from a server-side
        /// whitelist; DependsOn/Gloss were missing from it, so a planning agent could write both,
        /// receive Success, and have both silently discarded — leaving the plan graph showing the
        /// appended work as unconstrained and unexplained. This is the RECOMMENDED write path
        /// (update_checklist is documented as deprecated), so it was the likely path in practice.
        /// </summary>
        [Fact]
        public void AppendChecklistItems_PreservesDependsOnAndGloss()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;

            var json = "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"," +
                       "\"dependsOn\":[0,2]," +
                       "\"gloss\":{\"what\":\"Moves the code into its own file.\"," +
                       "\"why\":\"Gated on the inventory.\"," +
                       "\"without\":\"You find the coupling from compiler errors mid-move.\"}}]";

            var r = _svc.AppendChecklistItems(id, json);
            Assert.True(r.Success, r.Error);

            var appended = _svc.GetTask(id).GetChecklist().Last();
            Assert.Equal(new[] { 0, 2 }, appended.DependsOn);
            Assert.NotNull(appended.Gloss);
            Assert.Equal("Moves the code into its own file.", appended.Gloss.What);
            Assert.Equal("Gated on the inventory.", appended.Gloss.Why);
            Assert.Contains("compiler errors", appended.Gloss.Without, StringComparison.Ordinal);
        }

        [Fact]
        public void AppendChecklistItems_TreatsBlankGlossAsAbsent()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;

            var r = _svc.AppendChecklistItems(
                id,
                "[{\"item\":\"A\",\"gloss\":{\"what\":\"  \",\"why\":null,\"without\":\"\"}}]");
            Assert.True(r.Success, r.Error);

            // "nobody wrote an explanation" and "three empty strings" must not render differently.
            Assert.Null(_svc.GetTask(id).GetChecklist().Last().Gloss);
        }

        [Fact]
        public void AppendChecklistItems_DefaultsDependsOnToEmpty_NotNull()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;

            var r = _svc.AppendChecklistItems(id, "[{\"item\":\"A\"}]");
            Assert.True(r.Success, r.Error);

            // Empty means "no declared dependency" — the graph draws no edge for it. It must never
            // be null, or the builder's empty-list contract would depend on normalization order.
            var appended = _svc.GetTask(id).GetChecklist().Last();
            Assert.NotNull(appended.DependsOn);
            Assert.Empty(appended.DependsOn);
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

        // ── project-scoped active-task resolution (0cd2c868) ─────────────────────────────────────────

        // Drive a task to be the agent's single active task and return its id.
        private string MakeActive(string title, string assignee, string projectId)
        {
            var id = _svc.CreateTask(title, "d", assignee, projectId: projectId).TaskId;
            _svc.ClaimTask(id, assignee, null);
            _svc.UpdateTaskStatus(id, "in_progress");
            _svc.SetTaskActive(id, assignee);
            return id;
        }

        [Fact]
        public void GetMyActiveTask_MatchingProjectId_ReturnsTask()
        {
            var id = MakeActive("A", "diana", "projA");

            var task = _svc.GetMyActiveTask("diana", "projA");

            Assert.NotNull(task);
            Assert.Equal(id, task.Id);
        }

        [Fact]
        public void GetMyActiveTask_DifferentProjectId_ReturnsNull()
        {
            MakeActive("A", "diana", "projA");

            var task = _svc.GetMyActiveTask("diana", "projB");

            Assert.Null(task);
        }

        [Fact]
        public void GetMyActiveTask_NullProjectId_ReturnsTask_BackCompat()
        {
            var id = MakeActive("A", "diana", "projA");

            // The existing unscoped call site (optional param defaulted) must be byte-identical.
            var task = _svc.GetMyActiveTask("diana");

            Assert.NotNull(task);
            Assert.Equal(id, task.Id);
        }

        [Fact]
        public void GetMyActiveTask_TaskWithNullProjectId_ExcludedUnderNonEmptyScope()
        {
            // A task with no project is excluded by strict equality under a non-empty projectId,
            // but still resolves under the unscoped call.
            var id = MakeActive("A", "diana", null);

            Assert.Null(_svc.GetMyActiveTask("diana", "projA"));
            Assert.Equal(id, _svc.GetMyActiveTask("diana").Id);
        }

        [Fact]
        public void ResolveActiveTaskForAgent_HonorsProjectScope()
        {
            var id = MakeActive("A", "diana", "projA");

            Assert.Equal(id, _svc.ResolveActiveTaskForAgent("diana", "projA").Id);   // in scope
            Assert.Null(_svc.ResolveActiveTaskForAgent("diana", "projB"));           // out of scope
            Assert.Equal(id, _svc.ResolveActiveTaskForAgent("diana").Id);            // unscoped back-compat
        }

        [Fact]
        public void ResolveActiveTaskForAgent_HelperInOtherProject_ResolvesPerScope()
        {
            // diana is ASSIGNEE-active on T_Y (project Y) AND a HELPER (active task_worktrees row)
            // on T_X (project X). The scoped resolve must pick the RIGHT task per project — this is
            // the exact cross-project divergence the get_active_worktree pin (task 0cd2c868) guards:
            //   scope X -> the helper task, scope Y -> the assignee task, scope Z -> nothing.
            var tY = MakeActive("Y", "diana", "projY");                             // diana assignee-active in project Y
            var tX = _svc.CreateTask("X", "d", "bob", projectId: "projX").TaskId;   // a project-X task that is NOT diana's
            _db.SaveWorktreeRecord(tX, "diana", @"C:\wt\x", "task/x", isCanonical: false);  // diana helps on T_X

            Assert.Equal(tX, _svc.ResolveActiveTaskForAgent("diana", "projX").Id);  // helper task surfaces under scope X
            Assert.Equal(tY, _svc.ResolveActiveTaskForAgent("diana", "projY").Id);  // assignee task surfaces under scope Y
            Assert.Null(_svc.ResolveActiveTaskForAgent("diana", "projZ"));          // neither task is in scope Z
        }

        [Fact]
        public void GetMyActiveTask_ProjectIdMatch_IsCaseInsensitive()
        {
            // Mirrors the ClaimTask expectedProjectId gate (cf32b08f): the project-id domain is
            // case-insensitive, so a casing-divergent scope still resolves the same task.
            var id = MakeActive("A", "diana", "projA");

            Assert.Equal(id, _svc.GetMyActiveTask("diana", "PROJA").Id);
        }

        // ---- SetChecklistItemGloss: the narrow gloss write (task a455e295) ----

        /// <summary>
        /// Seed a task with a two-item checklist, both un-glossed.
        /// </summary>
        private string MakeGlossTask()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            var r = _svc.AppendChecklistItems(
                id,
                "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"}," +
                "{\"item\":\"Wire the host interface\",\"status\":\"pending\"}]");
            Assert.True(r.Success, r.Error);
            return id;
        }

        private static ChecklistItemGloss SampleGloss(string what = "Moves the code into its own file.") =>
            new ChecklistItemGloss
            {
                What = what,
                Why = "Gated on the inventory.",
                Without = "You find the coupling from compiler errors mid-move.",
            };

        /// <summary>
        /// THE test for this feature. A backfill agent reads the checklist, thinks for minutes, and
        /// writes. While it thinks, the coding agent moves item 0 forward and records notes.
        /// <para>A full-array write (<c>UpdateTaskChecklist</c>) carries the agent's stale snapshot
        /// back over the live list and silently reverts that transition — the item drops to pending
        /// and its notes and cycle count vanish, with no error anywhere. The narrow primitive cannot
        /// do this, because the only field it is able to write is the gloss.</para>
        /// <para>Demonstrated to FAIL against the full-array implementation — see
        /// <see cref="LostUpdate_IsExactlyWhatAFullArrayWriteDoes"/> directly below, which performs
        /// the stale write the naive way and asserts the damage.</para>
        /// </summary>
        [Fact]
        public void GlossWrite_FromAStaleSnapshot_DoesNotClobberAConcurrentTransition()
        {
            var id = MakeGlossTask();

            // T0 — the backfill agent reads. (It holds this list while it "thinks".)
            var staleSnapshot = _svc.GetTask(id).GetChecklist();
            Assert.Equal("pending", staleSnapshot[0].Status);

            // T1 — meanwhile, real work happens on item 0.
            var t = _svc.TransitionChecklistItem(id, 0, "coding", "starting", "diana");
            Assert.True(t.Success, t.Error);

            // T2 — the agent finally writes, still holding its T0 view of the world.
            var w = _svc.SetChecklistItemGloss(id, 0, SampleGloss());
            Assert.True(w.Success, w.Error);
            Assert.Equal(GlossWriteOutcome.Written, w.Outcome);

            // The transition survived, notes and all.
            var after = _svc.GetTask(id).GetChecklist();
            Assert.Equal("coding", after[0].Status);
            Assert.NotEmpty(after[0].Notes);
            Assert.Equal("Moves the code into its own file.", after[0].Gloss.What);
            Assert.True(after[0].Gloss.IsGenerated);
        }

        /// <summary>
        /// The negative half of the pair: the same sequence done the obvious way. This is what the
        /// feature would have shipped if the gloss reused the full-array path, and it exists so the
        /// test above is a demonstrated contrast rather than an assertion about code nobody ran.
        /// </summary>
        [Fact]
        public void LostUpdate_IsExactlyWhatAFullArrayWriteDoes()
        {
            var id = MakeGlossTask();

            var staleSnapshot = _svc.GetTask(id).GetChecklist();

            var t = _svc.TransitionChecklistItem(id, 0, "coding", "starting", "diana");
            Assert.True(t.Success, t.Error);

            // The naive backfill: attach the gloss to the stale snapshot and write the whole array.
            staleSnapshot[0].Gloss = SampleGloss();
            var w = _svc.UpdateTaskChecklist(
                id,
                System.Text.Json.JsonSerializer.Serialize(staleSnapshot));

            // It "succeeds" — which is the whole problem. Nothing anywhere reports a loss.
            Assert.True(w.Success, w.Error);

            var after = _svc.GetTask(id).GetChecklist();
            Assert.Equal("pending", after[0].Status);   // the transition is GONE
            Assert.Empty(after[0].Notes);               // and so are its notes
        }

        // ---- UpdateTaskChecklist merges instead of overwriting (task 2da6d8d9) ----

        /// <summary>
        /// A task whose second item carries BOTH plan-authoring fields: a machine-stamped gloss and
        /// a declared edge. Both are things <c>update_checklist</c>'s schema never mentioned.
        /// </summary>
        private string MakeGlossedTaskWithEdges()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            var r = _svc.AppendChecklistItems(
                id,
                "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"}," +
                "{\"item\":\"Wire the host interface\",\"status\":\"pending\",\"dependsOn\":[0]," +
                "\"gloss\":{\"what\":\"Lists the coupling.\",\"why\":\"Gated on the extraction.\"," +
                "\"without\":\"You find it from compiler errors mid-move.\"}}]");
            Assert.True(r.Success, r.Error);
            return id;
        }

        /// <summary>
        /// Exactly what an agent following the old schema wrote: item, status, notes. Nothing else,
        /// because nothing else was documented.
        /// </summary>
        private const string SchemaShapedRebuild =
            "[{\"item\":\"Extract TaskService\",\"status\":\"pending\",\"notes\":[]}," +
            "{\"item\":\"Wire the host interface\",\"status\":\"pending\",\"notes\":[]}]";

        /// <summary>
        /// THE regression. An array rebuilt from the three fields the schema described used to
        /// discard the gloss and every dependency edge, silently, with a success response.
        /// </summary>
        [Fact]
        public void FullReplace_InTheShapeTheSchemaDocumented_NoLongerErasesGlossOrEdges()
        {
            var id = MakeGlossedTaskWithEdges();

            var before = _svc.GetTask(id).GetChecklist();
            Assert.True(before[1].Gloss.IsGenerated);
            Assert.Equal(new[] { 0 }, before[1].DependsOn);

            var w = _svc.UpdateTaskChecklist(id, SchemaShapedRebuild);
            Assert.True(w.Success, w.Error);

            var after = _svc.GetTask(id).GetChecklist();
            Assert.Equal("Lists the coupling.", after[1].Gloss.What);
            Assert.True(after[1].Gloss.IsGenerated);          // the provenance stamp survived too
            Assert.Equal(new[] { 0 }, after[1].DependsOn);
        }

        /// <summary>
        /// The same rebuild done by someone who DID carry the gloss across — its three documented
        /// text fields, but not <c>source</c>, which the schema never mentioned. This is the exact
        /// payload CA-demoleg-CC wrote while repairing twelve glosses.
        /// </summary>
        private const string GlossRepairRebuild =
            "[{\"item\":\"Extract TaskService\",\"status\":\"pending\",\"notes\":[]}," +
            "{\"item\":\"Wire the host interface\",\"status\":\"pending\",\"notes\":[]," +
            "\"gloss\":{\"what\":\"Lists the coupling.\",\"why\":\"Gated on the extraction.\"," +
            "\"without\":\"You find it from compiler errors mid-move.\"}}]";

        /// <summary>
        /// The consequence that made this worth a ticket rather than a shrug. Losing the gloss was
        /// bad; losing its <c>source</c> was PERMANENT — the gloss normalized back to
        /// <c>authored</c> on read and <c>SetChecklistItemGloss</c> then refused to correct it for
        /// the rest of the task's life, reporting success each time it declined.
        /// </summary>
        /// <remarks>
        /// Note this uses <see cref="GlossRepairRebuild"/>, NOT <see cref="SchemaShapedRebuild"/>.
        /// The distinction is the whole test: a rebuild that drops the gloss ENTIRELY leaves nothing
        /// for the refusal to trigger on, so the correction succeeds and the test passes against the
        /// broken code for the wrong reason. The permanence bug bites only when the gloss TEXT
        /// survives and its provenance does not — which is precisely what an agent repairing a gloss
        /// through the documented schema produces. Written the first way, this assertion was green
        /// on the pre-fix build and proved nothing.
        /// </remarks>
        [Fact]
        public void FullReplace_DoesNotLeaveAGeneratedGlossPermanentlyUncorrectable()
        {
            var id = MakeGlossedTaskWithEdges();
            Assert.True(_svc.UpdateTaskChecklist(id, GlossRepairRebuild).Success);

            var w = _svc.SetChecklistItemGloss(id, 1, SampleGloss("Corrected afterwards."));

            Assert.True(w.Success, w.Error);
            Assert.Equal(GlossWriteOutcome.Written, w.Outcome);   // NOT SkippedAuthoredGlossPresent
            Assert.Equal("Corrected afterwards.", _svc.GetTask(id).GetChecklist()[1].Gloss.What);
        }

        /// <summary>
        /// Workflow state is carried forward on the same rule as the plan fields: an array that says
        /// only what each step IS leaves the status and notes history where it found them.
        /// </summary>
        [Fact]
        public void FullReplace_OmittingStatusAndNotes_KeepsTheWorkflowState()
        {
            var id = MakeGlossedTaskWithEdges();
            Assert.True(_svc.TransitionChecklistItem(id, 0, "coding", "starting", "diana").Success);

            var w = _svc.UpdateTaskChecklist(
                id, "[{\"item\":\"Extract TaskService\"},{\"item\":\"Wire the host interface\"}]");
            Assert.True(w.Success, w.Error);

            var after = _svc.GetTask(id).GetChecklist();
            Assert.Equal("coding", after[0].Status);
            Assert.NotEmpty(after[0].Notes);
        }

        /// <summary>
        /// The other half of the rule, and the one that keeps it honest: omission preserves, but a
        /// STATED value still wins — including an explicitly empty one. Without this the tool would
        /// have become unable to clear an edge at all, which is a different bug, not a fix.
        /// </summary>
        [Fact]
        public void FullReplace_StatingAnEmptyDependsOn_StillClearsTheEdges()
        {
            var id = MakeGlossedTaskWithEdges();

            var w = _svc.UpdateTaskChecklist(
                id,
                "[{\"item\":\"Extract TaskService\"}," +
                "{\"item\":\"Wire the host interface\",\"dependsOn\":[]}]");
            Assert.True(w.Success, w.Error);

            Assert.Empty(_svc.GetTask(id).GetChecklist()[1].DependsOn);
        }

        /// <summary>
        /// A gloss the caller DOES send is a fresh write, so it gets the same stamp the other two
        /// write paths apply. Anything else would leave this the one tool through which an agent can
        /// still brand its own words as a person's.
        /// </summary>
        [Fact]
        public void FullReplace_AStatedGlossWithNoSource_IsStampedGeneratedNotAuthored()
        {
            var id = MakeGlossTask();

            var w = _svc.UpdateTaskChecklist(
                id,
                "[{\"item\":\"Extract TaskService\",\"gloss\":{\"what\":\"a\",\"why\":\"b\",\"without\":\"c\"}}," +
                "{\"item\":\"Wire the host interface\"}]");
            Assert.True(w.Success, w.Error);

            Assert.True(_svc.GetTask(id).GetChecklist()[0].Gloss.IsGenerated);
            Assert.Equal(GlossWriteOutcome.Written, _svc.SetChecklistItemGloss(id, 0, SampleGloss()).Outcome);
        }

        /// <summary>
        /// The identity gate. Carry-forward is keyed on index AND exact item text, so a renamed step
        /// does NOT inherit the explanation and edges of whatever used to sit at its position.
        /// <para>This is the failure mode a looser match would introduce, and it is worse than the
        /// one being fixed: a lost gloss is visibly absent and can be rewritten, while a gloss
        /// silently attached to the wrong step reads as the planner's reasoning about work it never
        /// described. Misattribution is the thing this whole line of tickets exists to prevent.</para>
        /// </summary>
        [Fact]
        public void FullReplace_RenamingAnItem_DoesNotInheritThePreviousExplanation()
        {
            var id = MakeGlossedTaskWithEdges();

            var w = _svc.UpdateTaskChecklist(
                id,
                "[{\"item\":\"Extract TaskService\"},{\"item\":\"Something else entirely\"}]");
            Assert.True(w.Success, w.Error);

            var after = _svc.GetTask(id).GetChecklist();
            Assert.Null(after[1].Gloss);
            Assert.Empty(after[1].DependsOn);
        }

        /// <summary>
        /// Incidental to the merge, but a real latent crash: a null array element used to be stored
        /// verbatim, and the next <c>GetChecklist</c> — in whatever unrelated code reached it first
        /// — dereferenced it inside <c>NormalizeFromLegacy</c>. A checklist that cannot be READ is
        /// worse than one missing an element that was never valid.
        /// </summary>
        [Fact]
        public void FullReplace_WithANullArrayElement_NoLongerStoresAnUnreadableChecklist()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;

            var w = _svc.UpdateTaskChecklist(id, "[{\"item\":\"Real\"},null]");
            Assert.True(w.Success, w.Error);

            var after = _svc.GetTask(id).GetChecklist();
            Assert.Single(after);
            Assert.Equal("Real", after[0].Item);
        }

        /// <summary>
        /// Malformed JSON is refused up front rather than stored to explode later on someone else's
        /// read, and the existing checklist survives the refusal.
        /// </summary>
        [Fact]
        public void FullReplace_WithMalformedJson_IsRejectedAndLeavesTheChecklistIntact()
        {
            var id = MakeGlossTask();

            var w = _svc.UpdateTaskChecklist(id, "{not an array");

            Assert.False(w.Success);
            Assert.Equal(2, _svc.GetTask(id).GetChecklist().Count);
        }

        [Fact]
        public void GlossWrite_DoesNotOverwriteAnAuthoredGloss()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;

            // NOTE the explicit "source":"authored". Before task 5692f765 this fixture omitted it
            // and the test still passed — because append stamped everything as authored, so a
            // gloss labelled "A human wrote this." was believed despite no human being involved.
            // The invariant below is real and worth keeping; the fixture was not. Appending
            // without a source now yields a GENERATED gloss (see the sibling test), so a fixture
            // that means "a person wrote this" has to say so.
            _svc.AppendChecklistItems(
                id,
                "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"," +
                "\"gloss\":{\"what\":\"A human wrote this.\",\"why\":\"Because they understood it.\"," +
                "\"source\":\"authored\"}}]");

            var w = _svc.SetChecklistItemGloss(id, 0, SampleGloss("A machine wrote this."));

            // A correct no-op, NOT a failure: a caller that read this as an error would retry
            // forever against a task that is already right.
            Assert.True(w.Success, w.Error);
            Assert.Equal(GlossWriteOutcome.SkippedAuthoredGlossPresent, w.Outcome);
            Assert.Equal("A human wrote this.", _svc.GetTask(id).GetChecklist()[0].Gloss.What);
        }

        /// <summary>
        /// The scenario that produced task 5692f765: an agent authors a checklist through append,
        /// then cannot correct its own explanations.
        /// </summary>
        /// <remarks>
        /// Reported by a real caller after writing a 12-step ticket and getting "a human-authored
        /// explanation is already there and is never overwritten" on items nothing human had
        /// touched. This is the runtime counterpart to the source-contract assertions in
        /// GlossProvenanceContractTests — it exercises the actual append-then-correct round trip
        /// rather than the shape of the code that implements it.
        /// </remarks>
        [Fact]
        public void GlossAppendedWithoutASource_IsGenerated_AndCanStillBeCorrected()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            _svc.AppendChecklistItems(
                id,
                "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"," +
                "\"gloss\":{\"what\":\"An agent wrote this.\",\"why\":\"Restating importance, wrongly.\"}}]");

            var stored = _svc.GetTask(id).GetChecklist()[0].Gloss;
            Assert.True(
                stored.IsGenerated,
                "A gloss appended with no explicit source must be stamped generated. Stamping it " +
                "authored brands an agent's own words as a person's — the exact misattribution " +
                "ChecklistItemGloss.Source exists to prevent — and makes it permanently " +
                "uncorrectable, because SetChecklistItemGloss never overwrites a human.");

            var w = _svc.SetChecklistItemGloss(id, 0, SampleGloss("Corrected: gated on the inventory."));

            Assert.True(w.Success, w.Error);
            Assert.Equal(GlossWriteOutcome.Written, w.Outcome);
            Assert.Equal("Corrected: gated on the inventory.", _svc.GetTask(id).GetChecklist()[0].Gloss.What);
        }

        /// <summary>
        /// An agent relaying a person's words can still say so, and that claim is still honoured.
        /// </summary>
        /// <remarks>
        /// The counterpart to the test above, and the reason the append stamp preserves an explicit
        /// "authored" rather than branding every append generated. Stamping unconditionally would
        /// be the same misattribution in the opposite direction.
        /// </remarks>
        [Fact]
        public void GlossAppendedWithAnExplicitAuthoredSource_KeepsIt()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            _svc.AppendChecklistItems(
                id,
                "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"," +
                "\"gloss\":{\"what\":\"The owner's own words, relayed.\",\"source\":\"authored\"}}]");

            var stored = _svc.GetTask(id).GetChecklist()[0].Gloss;
            Assert.False(
                stored.IsGenerated,
                "An explicit 'authored' on an appended gloss must survive. An agent relaying a " +
                "person's words has to be able to say whose words they are.");
        }

        [Fact]
        public void GlossWrite_MayReplaceAPreviouslyGeneratedGloss()
        {
            var id = MakeGlossTask();
            Assert.True(_svc.SetChecklistItemGloss(id, 0, SampleGloss("First pass.")).Success);

            var w = _svc.SetChecklistItemGloss(id, 0, SampleGloss("Second pass, better."));

            Assert.Equal(GlossWriteOutcome.Written, w.Outcome);
            Assert.Equal("Second pass, better.", _svc.GetTask(id).GetChecklist()[0].Gloss.What);
        }

        [Fact]
        public void GlossWrite_AgainstAShrunkChecklist_IsRefusedNotRedirected()
        {
            var id = MakeGlossTask();

            // The agent planned to gloss item 1; the checklist shrank to one item while it thought.
            _svc.UpdateTaskChecklist(id, "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"}]");

            var w = _svc.SetChecklistItemGloss(id, 1, SampleGloss());

            Assert.False(w.Success);
            Assert.Contains("Invalid item index", w.Error);

            // Item 0 must NOT have received item 1's explanation.
            Assert.Null(_svc.GetTask(id).GetChecklist()[0].Gloss);
        }

        [Fact]
        public void GlossWrite_WritesOnlyTheGloss_AndLeavesEveryOtherFieldAlone()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            _svc.AppendChecklistItems(
                id,
                "[{\"item\":\"Extract TaskService\",\"status\":\"pending\",\"dependsOn\":[]}," +
                "{\"item\":\"Wire the host\",\"status\":\"pending\",\"dependsOn\":[0]}]");
            _svc.TransitionChecklistItem(id, 1, "coding", "starting", "diana");
            _svc.AssignChecklistItem(id, 1, "bob");

            var before = _svc.GetTask(id).GetChecklist()[1];

            _svc.SetChecklistItemGloss(id, 1, SampleGloss());

            var after = _svc.GetTask(id).GetChecklist()[1];
            Assert.Equal(before.Item, after.Item);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.AssignedTo, after.AssignedTo);
            Assert.Equal(before.CycleCount, after.CycleCount);
            Assert.Equal(before.Notes.Count, after.Notes.Count);
            Assert.Equal(before.DependsOn, after.DependsOn);
            Assert.NotNull(after.Gloss);

            // ...and the SIBLING item is untouched too.
            Assert.Null(_svc.GetTask(id).GetChecklist()[0].Gloss);
        }

        [Fact]
        public void GlossWrite_StampsGeneratedByDefault_ButHonoursAnExplicitAuthored()
        {
            var id = MakeGlossTask();

            _svc.SetChecklistItemGloss(id, 0, SampleGloss());
            Assert.True(_svc.GetTask(id).GetChecklist()[0].Gloss.IsGenerated);

            var authored = SampleGloss();
            authored.Source = ChecklistItemGloss.SourceAuthored;
            _svc.SetChecklistItemGloss(id, 1, authored);
            Assert.False(_svc.GetTask(id).GetChecklist()[1].Gloss.IsGenerated);
        }

        [Fact]
        public void GlossWrite_RejectsABlankGloss()
        {
            var id = MakeGlossTask();

            // Accepting this would let a caller "succeed" at erasing an explanation.
            var w = _svc.SetChecklistItemGloss(id, 0, new ChecklistItemGloss());

            Assert.False(w.Success);
            Assert.Contains("must carry text", w.Error);
        }

        [Fact]
        public void GlossWrite_DoesNotMoveTheCardOnTheLifecycleBoard()
        {
            var id = MakeGlossTask();
            var statusBefore = _svc.GetTask(id).AutoStatus;

            _svc.SetChecklistItemGloss(id, 0, SampleGloss());

            // Writing documentation is not progress.
            Assert.Equal(statusBefore, _svc.GetTask(id).AutoStatus);
        }

        // ---- Gloss backfill trigger (task a455e295) ----

        /// <summary>
        /// The trigger has to fire where the CONTENT is created. Hooking activation instead — the
        /// intuitive choice — mostly no-ops, because the standard flow activates a task before its
        /// checklist exists.
        /// </summary>
        [Fact]
        public void ChecklistWrite_RequestsAGlossBackfill()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            _host.GlossBackfillRequests.Clear();

            _svc.AppendChecklistItems(id, "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"}]");

            Assert.Contains(_host.GlossBackfillRequests, r => r.TaskId == id);
        }

        [Fact]
        public void FullChecklistReplace_AlsoRequestsAGlossBackfill()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            _host.GlossBackfillRequests.Clear();

            _svc.UpdateTaskChecklist(id, "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"}]");

            Assert.Contains(_host.GlossBackfillRequests, r => r.TaskId == id);
        }

        /// <summary>
        /// The backfill is documentation. If its path is broken, the user's actual checklist edit
        /// must still land — an explanation agent taking a checklist write down with it would be an
        /// absurd trade. Same posture as the RecordActivity sink in 7c59c004.
        /// </summary>
        [Fact]
        public void AThrowingGlossBackfill_CannotPoisonTheCommittedChecklistWrite()
        {
            var id = _svc.CreateTask("t", "d", "diana").TaskId;
            _host.ThrowFromRequestGlossBackfill = true;

            try
            {
                var r = _svc.AppendChecklistItems(id, "[{\"item\":\"Extract TaskService\",\"status\":\"pending\"}]");

                Assert.True(r.Success, r.Error);
                Assert.Single(_svc.GetTask(id).GetChecklist());
            }
            finally
            {
                _host.ThrowFromRequestGlossBackfill = false;
            }
        }

        [Fact]
        public void GlossBackfillPlanner_SelectsOnlyItemsThatNeedAnExplanation()
        {
            var checklist = new List<ChecklistItem>
            {
                new ChecklistItem { Item = "No gloss at all" },
                new ChecklistItem { Item = "Blank gloss", Gloss = new ChecklistItemGloss() },
                new ChecklistItem { Item = "Already explained", Gloss = new ChecklistItemGloss { What = "It does a thing." } },
                new ChecklistItem { Item = "   " },   // no real text — nothing to explain
            };

            var needed = MultiTerminal.MCPServer.Services.GlossBackfillPlanner.ItemsNeedingGloss(checklist);

            // 0 and 1 need one (a blank gloss counts as absent); 2 is done; 3 has no text.
            Assert.Equal(new[] { 0, 1 }, needed);
        }

        [Fact]
        public void GlossBackfillPlanner_TreatsAGeneratedGlossAsAlreadyWritten()
        {
            // Re-running would spend tokens rewriting machine prose with more machine prose. The
            // backfill fills a vacuum; it does not iterate.
            var checklist = new List<ChecklistItem>
            {
                new ChecklistItem
                {
                    Item = "Explained by a machine",
                    Gloss = new ChecklistItemGloss
                    {
                        What = "It does a thing.",
                        Source = ChecklistItemGloss.SourceGenerated,
                    },
                },
            };

            Assert.Empty(MultiTerminal.MCPServer.Services.GlossBackfillPlanner.ItemsNeedingGloss(checklist));
        }

        [Fact]
        public void GlossBackfillPrompt_NamesOnlyTheIndicesItShouldWrite_AndForbidsInvention()
        {
            var task = new KanbanTask { Id = "abc123", Title = "A task", Description = "Why it exists", Plan = "The plan" };
            var checklist = new List<ChecklistItem>
            {
                new ChecklistItem { Item = "Already explained", Gloss = new ChecklistItemGloss { What = "Does a thing." } },
                new ChecklistItem { Item = "Needs an explanation" },
            };

            var prompt = MultiTerminal.MCPServer.Services.GlossBackfillPlanner.BuildPrompt(task, checklist, new[] { 1 });

            Assert.Contains("WRITE A GLOSS FOR THESE INDICES ONLY: 1", prompt);
            Assert.Contains("abc123", prompt);
            Assert.Contains("The plan", prompt);

            // The two instructions that decide whether this feature helps or misleads.
            Assert.Contains("DO NOT RESTATE THE STEP", prompt);
            Assert.Contains("IF YOU CANNOT TELL, SAY SO", prompt);
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

            // Captured gloss-backfill requests as (taskId, reason), so the trigger is assertable
            // without spawning anything (task a455e295).
            public List<(string TaskId, string Reason)> GlossBackfillRequests { get; } = new List<(string, string)>();

            // When set, RequestGlossBackfill throws — models the backfill path failing, to prove it
            // cannot poison the committed checklist write that triggered it.
            public bool ThrowFromRequestGlossBackfill { get; set; }

            public void RaiseTasksUpdated(List<KanbanTask> tasks) => TasksUpdatedCount++;
            public void RaiseTaskClaimed(TaskClaimedEventArgs args) { }
            public void RaiseTaskActiveChanged(TaskActiveChangedEventArgs args) { }
            public void LogError(string message) { }
            public void LogWarning(string message) { }
            public void LogInfo(string message) { }
            public void LogTrace(string message) { }

            public void RequestGlossBackfill(string taskId, string reason)
            {
                if (ThrowFromRequestGlossBackfill)
                {
                    throw new InvalidOperationException("gloss backfill sink is down");
                }

                GlossBackfillRequests.Add((taskId, reason));
            }
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
            public TaskDoneMergeOutcome PerformPostPruneMergeAndFireReady(string taskId, KanbanTask task, string projectPath, string worktreePath)
                => PostPruneMergeOutcome;

            /// <summary>What the stubbed post-prune merge reports back (task b88e7017).</summary>
            public TaskDoneMergeOutcome PostPruneMergeOutcome { get; set; }
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
