using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Dialogs;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;
using FileProject = MultiTerminal.Models.Project;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 9f95ab0c, pipeline Run 3: the per-user cross-session create mutex (two brokers = two
    /// MultiTerminal processes in different Windows sessions, sharing one database and folder); the Project
    /// Manager's compensating rollback when its rich save fails after the broker create; the ID-bound undo;
    /// and the detector's per-row disk-work limit. Real brokers on temp SQLite and temp folders.
    /// </summary>
    public sealed class NewProjectRun3Tests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;
        private readonly string _root;
        private readonly string _folder;
        private readonly string _mutexName = $@"Local\mt-test-projectcreate-{Guid.NewGuid():N}";
        private readonly Func<string, bool> _originalExists;

        public NewProjectRun3Tests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_np3_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_np3_msg_{stamp}.db");
            _root = Path.Combine(Path.GetTempPath(), $"mt_np3_dir_{stamp}");
            _folder = Path.Combine(_root, "TestB");
            Directory.CreateDirectory(_folder);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
            _originalExists = ExistingProjectDetector.DirectoryExistsProbe;
        }

        public void Dispose()
        {
            ExistingProjectDetector.DirectoryExistsProbe = _originalExists;
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _dbPath, _msgDbPath })
            {
                foreach (var f in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(f)) File.Delete(f);
                }
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

        private MessageBroker NewBroker(ProjectService service) => new MessageBroker
        {
            ProjectService = service,
            ProjectCreateMutexName = _mutexName,
        };

        // ---- Cross-session mutex ----

        // Two brokers stand in for two MultiTerminal processes (console + RDP session): each has its OWN
        // _projectCreateLock, so only the shared named mutex can serialize them. The seam holds each create
        // after its folder check until both are there or 2s pass. Without the mutex both create.
        [Fact]
        public async Task Two_brokers_racing_one_folder_create_exactly_one_project()
        {
            using var db1 = new ProjectDatabase();
            using var service1 = new ProjectService(db1);
            using var db2 = new ProjectDatabase();
            using var service2 = new ProjectService(db2);
            using var broker1 = NewBroker(service1);
            using var broker2 = NewBroker(service2);
            using var barrier = new Barrier(2);
            broker1.TestHookAfterFolderCheck = () => barrier.SignalAndWait(TimeSpan.FromSeconds(2));
            broker2.TestHookAfterFolderCheck = () => barrier.SignalAndWait(TimeSpan.FromSeconds(2));
            var results = new CreateProjectResult[2];

            var t1 = Task.Run(() => results[0] = broker1.CreateProject("A", null, "test", _folder));
            var t2 = Task.Run(() => results[1] = broker2.CreateProject("B", null, "test", _folder));

            // Bounded wait, not a bare await: a deadlocked create fails the test instead of hanging the suite.
            var both = Task.WhenAll(t1, t2);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60));
            Assert.True(await Task.WhenAny(both, timeoutTask) == both, "creates did not finish");
            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Single(db1.GetAllProjects());
        }

        // Another process holds the mutex past the bounded wait: refuse, write nothing.
        [Fact]
        public void Create_is_refused_when_the_mutex_is_held_elsewhere()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            broker.ProjectCreateMutexTimeout = TimeSpan.FromMilliseconds(200);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using var other = new Mutex(false, _mutexName);
                other.WaitOne();
                held.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                other.ReleaseMutex();
            });
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            try
            {
                var result = broker.CreateProject("A", null, "test", _folder);

                Assert.False(result.Success);
                Assert.Contains("Another MultiTerminal instance is creating a project", result.Error);
                Assert.Empty(db.GetAllProjects());
                Assert.False(File.Exists(Path.Combine(_folder, ".claude", "project.json")));
            }
            finally
            {
                release.Set();
                holder.Join(TimeSpan.FromSeconds(10));
            }
        }

        // The mutex cannot even be opened (a namespace that does not exist): fail closed.
        [Fact]
        public void Create_is_refused_when_the_mutex_cannot_be_opened()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            broker.ProjectCreateMutexName = $@"NoSuchNamespace\mt-test-{Guid.NewGuid():N}";

            var create = broker.CreateProject("A", null, "test", _folder);
            var open = broker.RegisterExistingProject(_folder, "test");

            Assert.False(create.Success);
            Assert.Contains("could not be opened", create.Error);
            Assert.False(open.Success);
            Assert.Empty(db.GetAllProjects());
        }

        // The previous holder died holding the mutex: proceed (the folder check runs anyway).
        [Fact]
        public void Abandoned_mutex_is_taken_over_and_the_create_proceeds()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var dead = new Thread(() =>
            {
                var m = new Mutex(false, _mutexName);
                m.WaitOne(); // exits without ReleaseMutex: abandoned
                GC.KeepAlive(m);
            });
            dead.Start();
            dead.Join();

            var result = broker.CreateProject("A", null, "test", _folder);

            Assert.True(result.Success, result.Error);
            Assert.Single(db.GetAllProjects());
        }

        // ---- Project Manager compensating rollback ----

        [Fact]
        public void Failed_rich_save_rolls_the_create_back_so_a_retry_starts_clean()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var project = new FileProject { Id = "dialog-guid", Name = "Fresh", Path = _folder };
            string uncommitted = null;

            string first = EditProjectDialog.CommitNewProject(project,
                p => ProjectManagerDialog.CreateThroughBroker(broker, p),
                _ => throw new IOException("disk full"),
                p => ProjectManagerDialog.RollBackThroughBroker(broker, p),
                ref uncommitted);

            Assert.Contains("removed again", first);
            Assert.Null(uncommitted);
            Assert.Empty(db.GetAllProjects());
            Assert.False(File.Exists(Path.Combine(_folder, ".claude", "project.json")));

            string retry = EditProjectDialog.CommitNewProject(project,
                p => ProjectManagerDialog.CreateThroughBroker(broker, p),
                p => db.SaveRichProject(p),
                p => ProjectManagerDialog.RollBackThroughBroker(broker, p),
                ref uncommitted);

            Assert.Null(retry);
            Assert.Single(db.GetAllProjects());
            Assert.Equal(db.GetAllProjects()[0].Id, service.LoadProject(_folder).Id);
        }

        // Run 4 (replaces Run 3's "retry re-runs only the save under the kept id", which the debugger
        // failed: an upsert under a kept id configures a project the folder check never re-approved).
        // Rollback fails: the id is kept; while the undo keeps failing a retry does NOTHING else.
        [Fact]
        public void Kept_id_retry_reruns_the_undo_and_does_nothing_else_while_it_fails()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var project = new FileProject { Id = "dialog-guid", Name = "Fresh", Path = _folder };
            string uncommitted = null;
            int guardCalls = 0, saveCalls = 0, rollbackCalls = 0;
            string Guard(FileProject p) { guardCalls++; return ProjectManagerDialog.CreateThroughBroker(broker, p); }
            string FailingRollback(FileProject p) { rollbackCalls++; return "simulated rollback failure"; }

            string first = EditProjectDialog.CommitNewProject(project, Guard,
                _ => throw new IOException("disk full"), FailingRollback, ref uncommitted);
            Assert.Contains("could not be removed", first);
            string createdId = uncommitted;
            Assert.NotNull(createdId);

            string retry = EditProjectDialog.CommitNewProject(project, Guard,
                _ => saveCalls++, FailingRollback, ref uncommitted);

            Assert.Contains("still could not be removed", retry);
            Assert.Equal(1, guardCalls);
            Assert.Equal(0, saveCalls);
            Assert.Equal(2, rollbackCalls);
            Assert.Equal(createdId, uncommitted);
            Assert.Single(db.GetAllProjects());
        }

        // Once the undo succeeds, the retry creates afresh through the guard: a NEW id, one row.
        [Fact]
        public void Kept_id_retry_creates_afresh_once_the_undo_succeeds()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var project = new FileProject { Id = "dialog-guid", Name = "Fresh", Path = _folder };
            string uncommitted = null;

            EditProjectDialog.CommitNewProject(project,
                p => ProjectManagerDialog.CreateThroughBroker(broker, p),
                _ => throw new IOException("disk full"), _ => "simulated rollback failure", ref uncommitted);
            string leftover = uncommitted;
            Assert.NotNull(leftover);

            string retry = EditProjectDialog.CommitNewProject(project,
                p => ProjectManagerDialog.CreateThroughBroker(broker, p),
                p => db.SaveRichProject(p),
                p => ProjectManagerDialog.RollBackThroughBroker(broker, p),
                ref uncommitted);

            Assert.Null(retry);
            Assert.Null(uncommitted);
            Assert.NotEqual(leftover, project.Id);
            var rows = db.GetAllProjects();
            Assert.Single(rows);
            Assert.Equal(project.Id, rows[0].Id);
            Assert.Equal(project.Id, service.LoadProject(_folder).Id);
        }

        // The undo is ID-bound: a project.json that no longer carries the created id is left alone.
        [Fact]
        public void Undo_removes_only_what_carries_the_created_id()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var created = broker.CreateProject("A", null, "test", _folder);
            Assert.True(created.Success, created.Error);
            string jsonPath = Path.Combine(_folder, ".claude", "project.json");
            File.WriteAllText(jsonPath, File.ReadAllText(jsonPath).Replace(created.ProjectId, "0therid0"));
            byte[] before = File.ReadAllBytes(jsonPath);

            string error = broker.UndoProjectCreate(created.ProjectId);

            Assert.Null(error);
            Assert.Empty(db.GetAllProjects());
            Assert.Equal(before, File.ReadAllBytes(jsonPath));
        }

        // ---- Disk work per registered row ----

        // Rows on a network share or another drive are compared lexically only: no existence check (the
        // first disk touch, which stalls for the SMB timeout on an offline share).
        [Fact]
        public void Rows_off_the_target_drive_cost_no_disk_work()
        {
            var probed = new List<string>();
            ExistingProjectDetector.DirectoryExistsProbe = p => { lock (probed) probed.Add(p); return Directory.Exists(p); };
            string otherDrive = Path.GetPathRoot(_folder).StartsWith("Q", StringComparison.OrdinalIgnoreCase) ? @"R:\Elsewhere\TestB" : @"Q:\Elsewhere\TestB";
            var rows = new List<(string, string, string)>
            {
                ("11111111", "Offline", @"\\mt-test-no-such-host-9f95\share\TestB"),
                ("22222222", "OtherDrive", otherDrive),
            };

            var match = ExistingProjectDetector.Detect(_folder, ProjectFileState.Absent, null, rows);

            Assert.False(match.Exists);
            Assert.DoesNotContain(probed, p => p.StartsWith(@"\\", StringComparison.Ordinal));
            Assert.DoesNotContain(probed, p => p.StartsWith(otherDrive.Substring(0, 2), StringComparison.OrdinalIgnoreCase));
        }
    }
}
