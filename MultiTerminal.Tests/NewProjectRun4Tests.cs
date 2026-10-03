using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Dialogs;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 9f95ab0c, pipeline Run 4: the undo/retry path never throws out of the dialog, deletes
    /// project.json only after the row delete is confirmed, and raises the removal notifications; and
    /// ProjectDatabase.DeleteProject removes child rows itself, because SQLite foreign keys are off and
    /// ON DELETE CASCADE never runs. Real broker / database on temp files.
    /// </summary>
    public sealed class NewProjectRun4Tests : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _msgDbPath;
        private readonly string _root;
        private readonly string _folder;
        private readonly string _mutexName = $@"Local\mt-test-projectcreate-{Guid.NewGuid():N}";

        public NewProjectRun4Tests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_np4_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_np4_msg_{stamp}.db");
            _root = Path.Combine(Path.GetTempPath(), $"mt_np4_dir_{stamp}");
            _folder = Path.Combine(_root, "TestB");
            Directory.CreateDirectory(_folder);
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

        private string JsonPath => Path.Combine(_folder, ".claude", "project.json");

        // ---- Rollback ordering ----

        [Fact]
        public void Row_delete_failure_leaves_project_json_and_returns_the_error()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var created = broker.CreateProject("A", null, "test", _folder);
            Assert.True(created.Success, created.Error);
            byte[] before = File.ReadAllBytes(JsonPath);
            broker.TestHookDeleteProjectRow = _ => throw new IOException("database is locked");

            string error = broker.UndoProjectCreate(created.ProjectId);

            Assert.NotNull(error);
            Assert.Contains("database row", error);
            Assert.Equal(before, File.ReadAllBytes(JsonPath));
            Assert.NotNull(db.GetRichProject(created.ProjectId));
        }

        // ---- No throw out of the dialog ----

        [Fact]
        public void Undo_returns_an_error_instead_of_throwing_and_stays_recoverable()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var created = broker.CreateProject("A", null, "test", _folder);
            Assert.True(created.Success, created.Error);
            broker.TestHookBeforeUndo = _ => throw new SQLiteException("simulated read failure");

            string error = broker.UndoProjectCreate(created.ProjectId);

            Assert.NotNull(error);
            Assert.Contains("simulated read failure", error);
            Assert.NotNull(db.GetRichProject(created.ProjectId));

            broker.TestHookBeforeUndo = null;
            Assert.Null(broker.UndoProjectCreate(created.ProjectId));
            Assert.Empty(db.GetAllProjects());
            Assert.False(File.Exists(JsonPath));
        }

        [Fact]
        public void A_throwing_rollback_delegate_is_reported_not_thrown()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var project = new Project { Id = "dialog-guid", Name = "Fresh", Path = _folder };
            string uncommitted = null;

            string message = EditProjectDialog.CommitNewProject(project,
                p => ProjectManagerDialog.CreateThroughBroker(broker, p),
                _ => throw new IOException("disk full"),
                _ => throw new InvalidOperationException("rollback blew up"),
                ref uncommitted);

            Assert.Contains("could not be removed", message);
            Assert.Contains("rollback blew up", message);
            Assert.NotNull(uncommitted);

            // Recoverable: the next attempt re-runs the (now working) undo, then creates afresh.
            string retry = EditProjectDialog.CommitNewProject(project,
                p => ProjectManagerDialog.CreateThroughBroker(broker, p),
                p => db.SaveRichProject(p),
                p => ProjectManagerDialog.RollBackThroughBroker(broker, p),
                ref uncommitted);
            Assert.Null(retry);
            Assert.Single(db.GetAllProjects());
        }

        // ---- Removal notifications after an undo ----

        [Fact]
        public void Undo_raises_the_same_notifications_as_a_project_removal()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var created = broker.CreateProject("A", null, "test", _folder);
            Assert.True(created.Success, created.Error);
            string removedId = null;
            int registryChanged = 0;
            service.ProjectRemoved += (_, e) => removedId = e.Project.Id;
            service.RegistryChangedExternally += (_, _) => registryChanged++;

            Assert.Null(broker.UndoProjectCreate(created.ProjectId));

            Assert.Equal(created.ProjectId, removedId);
            Assert.Equal(1, registryChanged);
        }

        // Run 5: a throwing ProjectRemoved subscriber cannot stop the later subscribers,
        // RegistryChangedExternally, or ProjectsUpdated; the undo itself still succeeds (the data is removed).
        [Fact]
        public void A_throwing_removal_subscriber_cannot_suppress_the_other_notifications()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var created = broker.CreateProject("A", null, "test", _folder);
            Assert.True(created.Success, created.Error);
            int laterRemoved = 0, registryChanged = 0, projectsUpdated = 0;
            service.ProjectRemoved += (_, _) => throw new InvalidOperationException("bad subscriber");
            service.ProjectRemoved += (_, _) => laterRemoved++;
            service.RegistryChangedExternally += (_, _) => registryChanged++;
            broker.ProjectsUpdated += (_, _) => projectsUpdated++;

            Assert.Null(broker.UndoProjectCreate(created.ProjectId));

            Assert.Equal(1, laterRemoved);
            Assert.Equal(1, registryChanged);
            Assert.True(projectsUpdated >= 1, "ProjectsUpdated was not raised");
            Assert.Empty(db.GetAllProjects());
        }

        // Run 5: the first undo deletes the row but cannot delete project.json (held open without
        // FILE_SHARE_DELETE). The retry, with the row gone, must still finish that delete through the
        // folder the dialog passes, not report success over a project.json naming a nonexistent project.
        [Fact]
        public void Retry_finishes_a_project_json_delete_that_failed_after_the_row_was_removed()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = NewBroker(service);
            var created = broker.CreateProject("A", null, "test", _folder);
            Assert.True(created.Success, created.Error);
            var dialogProject = new Project { Id = created.ProjectId, Name = "A", Path = _folder };

            using (new FileStream(JsonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string first = ProjectManagerDialog.RollBackThroughBroker(broker, dialogProject);

                Assert.NotNull(first);
                Assert.Contains("project.json", first);
                Assert.Null(db.GetRichProject(created.ProjectId));
                Assert.True(File.Exists(JsonPath));
            }

            string retry = ProjectManagerDialog.RollBackThroughBroker(broker, dialogProject);

            Assert.Null(retry);
            Assert.False(File.Exists(JsonPath));
        }

        // ---- Child rows ----

        private int ChildRows(string table, string projectId)
        {
            using var conn = new SQLiteConnection($"Data Source={_dbPath}");
            conn.Open();
#pragma warning disable CA2100 // table name from ProjectDatabase.ProjectChildTables
            using var cmd = new SQLiteCommand($"SELECT COUNT(*) FROM {table} WHERE project_id = @id", conn);
#pragma warning restore CA2100
            cmd.Parameters.AddWithValue("@id", projectId);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void SeedChildren(ProjectDatabase db, string projectId)
        {
            db.SaveProjectAgent(new ProjectAgent { ProjectId = projectId, AgentName = "Alice", Role = "lead" });
            db.SaveProjectMcpServer(new ProjectMcpServer { ProjectId = projectId, ServerName = "sqlite" });
            db.SaveProjectSpecialistAgent(new ProjectSpecialistAgent { ProjectId = projectId, AgentType = "verifier" });
            db.SaveProjectPath(new ProjectPath { ProjectId = projectId, PathType = "deploy", PathValue = @"C:\deploy" });
            db.SaveProjectPrompt(new ProjectPromptEntry { ProjectId = projectId, PromptType = "startup", PromptText = "hello" });
            db.SaveProjectSkill(new ProjectSkill { ProjectId = projectId, SkillName = "pipeline" });
        }

        [Fact]
        public void DeleteProject_removes_every_child_row_and_only_that_projects()
        {
            using var db = new ProjectDatabase();
            var doomed = Project.Create("Doomed", Path.Combine(_root, "Doomed"));
            var kept = Project.Create("Kept", Path.Combine(_root, "Kept"));
            db.SaveRichProject(doomed);
            db.SaveRichProject(kept);
            SeedChildren(db, doomed.Id);
            SeedChildren(db, kept.Id);
            foreach (var table in ProjectDatabase.ProjectChildTables)
                Assert.Equal(1, ChildRows(table, doomed.Id));

            Assert.True(db.DeleteProject(doomed.Id));

            foreach (var table in ProjectDatabase.ProjectChildTables)
            {
                Assert.True(ChildRows(table, doomed.Id) == 0, $"orphan rows left in {table}");
                Assert.True(ChildRows(table, kept.Id) == 1, $"{table} lost another project's row");
            }
            Assert.Null(db.GetRichProject(doomed.Id));
            Assert.NotNull(db.GetRichProject(kept.Id));
        }
    }
}
