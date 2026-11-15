using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;
using FileProject = MultiTerminal.Models.Project;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// GitHub #102 (ticket da0fcff8): a deleted project's leftover .claude/project.json used to be re-adopted
    /// silently (same id, pin, settings and tasks). Now the create asks Restore or New, auto-add on cd skips a
    /// deleted id, and the delete asks about the tasks.
    /// </summary>
    public sealed class DeletedProjectReadoptTests : IDisposable
    {
        private const string OldId = "0c1b50f6";
        private readonly string _dbPath;
        private readonly string _msgDbPath;
        private readonly string _folder;

        public DeletedProjectReadoptTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_readopt_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_readopt_msg_{stamp}.db");
            _folder = Path.Combine(Path.GetTempPath(), $"mt_readopt_dir_{stamp}", "FlexFriend");
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

            var root = Path.GetDirectoryName(_folder);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            GC.SuppressFinalize(this);
        }

        // FlexFriend registered (row + project.json), then deleted in MT with the file left behind.
        private static void RegisterThenDelete(ProjectService service, MessageBroker broker, string folder)
        {
            var p = FileProject.Create("FlexFriend", folder);
            p.Id = OldId;
            p.CreatedAt = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
            service.SaveProject(p);
            Assert.True(broker.DeleteProject(OldId, "test").Success);
            Assert.True(File.Exists(Path.Combine(folder, ".claude", "project.json")));
        }

        [Fact]
        public void Create_on_a_deleted_projects_folder_asks_instead_of_adopting()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            RegisterThenDelete(service, broker, _folder);

            var result = broker.CreateProject("FlexFriend", null, "agent", _folder);

            Assert.False(result.Success);
            Assert.True(result.FolderOccupied);
            Assert.Equal(OldId, result.ExistingProjectId);
            Assert.Contains("was deleted from MultiTerminal on", result.Error);
            Assert.Contains("existingProject=\"restore\"", result.Error);
            Assert.Null(db.GetRichProject(OldId));
        }

        [Fact]
        public void Start_new_gives_a_new_id_and_keeps_the_old_file()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            RegisterThenDelete(service, broker, _folder);

            var result = broker.CreateProject("FlexFriend", null, "agent", _folder, existingProject: "new");

            Assert.True(result.Success, result.Error);
            Assert.NotEqual(OldId, result.ProjectId);
            Assert.True(File.Exists(Path.Combine(_folder, ".claude", "project.json.old")));
            Assert.Equal(result.ProjectId, service.LoadProject(_folder).Id);
            Assert.Null(db.GetRichProject(OldId));
        }

        [Fact]
        public void Restore_keeps_the_id_and_forgets_the_deletion()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            RegisterThenDelete(service, broker, _folder);
            Assert.NotNull(db.GetDeletedAt(OldId));

            var result = broker.CreateProject("FlexFriend", null, "agent", _folder, existingProject: "restore");

            Assert.True(result.Success, result.Error);
            Assert.Equal(OldId, result.ProjectId);
            Assert.NotNull(db.GetRichProject(OldId));
            Assert.Null(db.GetDeletedAt(OldId));
        }

        [Fact]
        public void Auto_add_on_cd_skips_a_deleted_project_but_still_adds_an_unknown_one()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            RegisterThenDelete(service, broker, _folder);

            Assert.False(service.AutoRegisterDiscoveredProject(_folder, service.LoadProject(_folder)));
            Assert.Null(db.GetRichProject(OldId));

            // A folder MT has never seen (e.g. from another machine) is still added.
            string other = Path.Combine(Path.GetDirectoryName(_folder), "Fresh");
            Directory.CreateDirectory(other);
            var fresh = FileProject.Create("Fresh", other);
            fresh.Id = "1a2b3c4d";
            Directory.CreateDirectory(Path.Combine(other, ".claude"));
            File.WriteAllText(Path.Combine(other, ".claude", "project.json"),
                $"{{\"id\":\"{fresh.Id}\",\"name\":\"Fresh\",\"createdAt\":\"2026-11-01T00:00:00Z\"}}");
            Assert.True(service.AutoRegisterDiscoveredProject(other, service.LoadProject(other)));
            Assert.NotNull(db.GetRichProject(fresh.Id));
        }

        private const string NoTombstonesTrigger =
            "CREATE TRIGGER no_tombstones BEFORE INSERT ON deleted_projects BEGIN SELECT RAISE(ABORT, 'disk full'); END;";

        private const string NoProjectsTrigger =
            "CREATE TRIGGER no_projects BEFORE INSERT ON projects BEGIN SELECT RAISE(ABORT, 'disk full'); END;";

        // Injects a write failure: the trigger text is one of the constants above, never input.
        private void AddFailingTrigger(bool tombstones)
        {
            using var c = new SQLiteConnection($"Data Source={_dbPath}");
            c.Open();
            using var cmd = c.CreateCommand();
            if (tombstones)
                cmd.CommandText = NoTombstonesTrigger;
            else
                cmd.CommandText = NoProjectsTrigger;
            cmd.ExecuteNonQuery();
        }

        // Pipeline Run 1 (Codex): a delete that cannot record its tombstone must fail, not succeed and leave a
        // project.json that auto-add on cd would re-adopt.
        [Fact]
        public void Delete_fails_and_keeps_the_project_when_the_deletion_cannot_be_recorded()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            var p = FileProject.Create("FlexFriend", _folder);
            p.Id = OldId;
            service.SaveProject(p);
            AddFailingTrigger(tombstones: true);

            var result = broker.DeleteProject(OldId, "test");

            Assert.False(result.Success);
            Assert.NotNull(db.GetRichProject(OldId));
        }

        // Pipeline Run 1 (debugger + Codex): "new" puts the old file back when the create fails afterwards.
        [Fact]
        public void Start_new_puts_the_old_file_back_when_the_create_fails()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            RegisterThenDelete(service, broker, _folder);
            AddFailingTrigger(tombstones: false);

            var result = broker.CreateProject("FlexFriend", null, "agent", _folder, existingProject: "new");

            Assert.False(result.Success);
            Assert.True(File.Exists(Path.Combine(_folder, ".claude", "project.json")));
            Assert.False(File.Exists(Path.Combine(_folder, ".claude", "project.json.old")));
            Assert.Equal(OldId, service.LoadProject(_folder).Id);
        }

        // Pipeline Run 1 (debugger): an id that differs only in case is still the deleted project.
        [Fact]
        public void Auto_add_skips_a_deleted_project_whose_id_differs_in_case()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            RegisterThenDelete(service, broker, _folder);
            var upper = service.LoadProject(_folder);
            upper.Id = OldId.ToUpperInvariant();

            Assert.True(service.IsDeletedProject(upper.Id));
            Assert.False(service.AutoRegisterDiscoveredProject(_folder, upper));
        }

        // Pipeline Run 2 (Codex): UnregisterProject removes the row and then raises its events. A throwing
        // listener must not make the delete forget its tombstone, or the leftover file is re-adopted.
        [Fact]
        public void A_failing_removal_listener_keeps_the_tombstone_of_a_committed_delete()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            var p = FileProject.Create("FlexFriend", _folder);
            p.Id = OldId;
            service.SaveProject(p);
            service.ProjectRemoved += (_, _) => throw new InvalidOperationException("listener failed");

            var result = broker.DeleteProject(OldId, "test");

            Assert.True(result.Success, result.Error);
            Assert.Null(db.GetRichProject(OldId));
            Assert.NotNull(db.GetDeletedAt(OldId));
            Assert.False(service.AutoRegisterDiscoveredProject(_folder, service.LoadProject(_folder)));
        }

        // Pipeline Run 2 (Codex): a deleted_projects table created without COLLATE NOCASE (CREATE TABLE IF NOT
        // EXISTS never alters it) still matches an id that differs only in case.
        [Fact]
        public void A_table_created_without_nocase_still_matches_ids_in_any_case()
        {
            using (var c = new SQLiteConnection($"Data Source={_dbPath}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = OldSchemaTable;
                cmd.ExecuteNonQuery();
            }

            using var db = new ProjectDatabase();
            db.RecordDeletedProject(OldId, "FlexFriend", _folder);

            Assert.NotNull(db.GetDeletedAt(OldId.ToUpperInvariant()));
            db.ForgetDeletedProject(OldId.ToUpperInvariant());
            Assert.Null(db.GetDeletedAt(OldId));
        }

        private const string OldSchemaTable =
            "CREATE TABLE deleted_projects (id TEXT PRIMARY KEY, name TEXT, path TEXT, deleted_at DATETIME NOT NULL);";

        [Theory]
        [InlineData(false, 2)]
        [InlineData(true, 0)]
        public void Delete_keeps_or_deletes_the_projects_tasks_as_asked(bool deleteTasks, int left)
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            var p = FileProject.Create("FlexFriend", _folder);
            p.Id = OldId;
            service.SaveProject(p);
            broker.CreateTask("one", "d", "test", projectId: OldId);
            broker.CreateTask("two", "d", "test", projectId: OldId);

            Assert.True(broker.DeleteProject(OldId, "test", deleteTasks: deleteTasks).Success);

            Assert.Equal(left, broker.GetTasks(OldId).Count);
        }
    }
}
