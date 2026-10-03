using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Models;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;
using FileProject = MultiTerminal.Models.Project;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 9f95ab0c, observed 2026-10-02: New Project named "TestC" on TestB's folder kept TestB's id
    /// and wrote over its name, description and createdAt. These run a REAL MessageBroker and
    /// ProjectService against a temp SQLite file and a temp project folder.
    /// </summary>
    public sealed class NewProjectExistingFolderBrokerTests : IDisposable
    {
        private static readonly DateTime OriginalCreatedAt = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        private readonly string _dbPath;
        private readonly string _msgDbPath;
        private readonly string _folder;

        public NewProjectExistingFolderBrokerTests()
        {
            var stamp = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_newproj_{stamp}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"mt_newproj_msg_{stamp}.db");
            _folder = Path.Combine(Path.GetTempPath(), $"mt_newproj_dir_{stamp}", "TestB");
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

        // TestB as it stood: DB row + project.json, a description, an old createdAt and a lead.
        private static FileProject SeedTestB(ProjectService service, string folder)
        {
            var p = FileProject.Create("TestB", folder);
            p.Id = "ac8a793b";
            p.Description = "The original description";
            p.CreatedAt = OriginalCreatedAt;
            p.TeamLead = "Alice";
            service.SaveProject(p);
            return p;
        }

        // expectLead=false for a project.json-only folder: TeamLead lives in the database only (project.json
        // does not carry it), so such a folder has no lead to preserve.
        private static void AssertUnchanged(ProjectService service, ProjectDatabase db, string folder, bool expectLead = true)
        {
            var row = db.GetRichProject("ac8a793b");
            Assert.NotNull(row);
            Assert.Equal("TestB", row.Name);
            Assert.Equal("The original description", row.Description);
            if (expectLead) Assert.Equal("Alice", row.TeamLead);

            var file = service.LoadProject(folder);
            Assert.Equal("ac8a793b", file.Id);
            Assert.Equal("TestB", file.Name);
            Assert.Equal("The original description", file.Description);
            Assert.Equal(OriginalCreatedAt, file.CreatedAt.ToUniversalTime());
        }

        // The exact observed sequence, through the reuse arm that New Project used to take.
        [Fact]
        public void Reuse_on_an_existing_project_never_rewrites_name_description_createdAt_or_lead()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            SeedTestB(service, _folder);
            using var broker = new MessageBroker { ProjectService = service };

            var result = broker.CreateProject("TestC", null, "new-project-dialog", _folder,
                teamLead: "", allowReuseExisting: true);

            Assert.True(result.Success, result.Error);
            Assert.Equal("ac8a793b", result.ProjectId);
            AssertUnchanged(service, db, _folder);
        }

        [Fact]
        public void Create_on_a_folder_with_project_json_is_refused_with_the_existing_id()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            SeedTestB(service, _folder);
            using var broker = new MessageBroker { ProjectService = service };

            var result = broker.CreateProject("TestC", null, "new-project-dialog", _folder);

            Assert.False(result.Success);
            Assert.Equal("ac8a793b", result.ExistingProjectId);
            AssertUnchanged(service, db, _folder);
        }

        // A DB row pointing at the folder with NO project.json was invisible to the old check, so a second
        // project got created on the same folder. Reached here through a trailing separator and upper case.
        [Fact]
        public void Create_on_a_folder_known_only_to_the_database_is_refused()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            var first = broker.CreateProject("Testing", "fixture", "test", _folder);
            Assert.True(first.Success, first.Error);
            File.Delete(Path.Combine(_folder, ".claude", "project.json"));
            Assert.Null(service.LoadProject(_folder));

            var result = broker.CreateProject("mt-stress-fixture", null, "test", _folder.ToUpperInvariant() + Path.DirectorySeparatorChar);

            Assert.False(result.Success);
            Assert.Equal(first.ProjectId, result.ExistingProjectId);
            Assert.Single(db.GetAllProjects());
        }

        // A project.json-only folder (no DB row) adopted for "Open existing": the row is copied FROM the file.
        [Fact]
        public void Adopting_a_project_json_only_folder_registers_it_from_the_file()
        {
            using (var seedDb = new ProjectDatabase())
            using (var seedService = new ProjectService(seedDb))
            {
                SeedTestB(seedService, _folder);
                Assert.True(seedDb.DeleteProject("ac8a793b"));
            }
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };

            var result = broker.CreateProject("TestC", null, "new-project-dialog", _folder, allowReuseExisting: true);

            Assert.True(result.Success, result.Error);
            Assert.Equal("ac8a793b", result.ProjectId);
            AssertUnchanged(service, db, _folder, expectLead: false);
        }

        // "Rename existing": an explicit UpdateProject, which must reach project.json as well as the row.
        [Fact]
        public void Rename_updates_the_database_row_and_project_json_and_keeps_everything_else()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            SeedTestB(service, _folder);
            using var broker = new MessageBroker { ProjectService = service };

            var result = broker.UpdateProject("ac8a793b", "TestC", null, "new-project-dialog");

            Assert.True(result.Success, result.Error);
            var row = db.GetRichProject("ac8a793b");
            Assert.Equal("TestC", row.Name);
            Assert.Equal("The original description", row.Description);
            Assert.Equal("Alice", row.TeamLead);
            var file = service.LoadProject(_folder);
            Assert.Equal("TestC", file.Name);
            Assert.Equal("The original description", file.Description);
            Assert.Equal(OriginalCreatedAt, file.CreatedAt.ToUniversalTime());
        }

        // ---- Pipeline Run 1 ----

        // A copy of TestB's folder carries TestB's id. Every create/register path must refuse, and neither
        // TestB (row + its own project.json) nor the copy's file may change.
        [Fact]
        public void Copied_project_json_is_refused_by_every_path_and_nothing_changes()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            SeedTestB(service, _folder);
            string copy = Path.Combine(Path.GetDirectoryName(_folder), "TestB-copy");
            Directory.CreateDirectory(Path.Combine(copy, ".claude"));
            string copyJson = Path.Combine(copy, ".claude", "project.json");
            File.Copy(Path.Combine(_folder, ".claude", "project.json"), copyJson);
            byte[] copyBytes = File.ReadAllBytes(copyJson);
            using var broker = new MessageBroker { ProjectService = service };

            Assert.Equal(ExistingProjectProblem.IdRegisteredElsewhere, broker.FindExistingProjectAtPath(copy).Problem);
            var create = broker.CreateProject("TestC", null, "new-project-dialog", copy);
            var reuse = broker.CreateProject("TestC", null, "new-project-dialog", copy, allowReuseExisting: true);
            var open = broker.RegisterExistingProject(copy, "new-project-dialog");

            Assert.False(create.Success);
            Assert.True(create.FolderOccupied);
            Assert.False(reuse.Success);
            Assert.False(open.Success);
            AssertUnchanged(service, db, _folder);
            Assert.True(ExistingProjectDetector.PathsEqual(_folder, db.GetRichProject("ac8a793b").Path));
            Assert.Single(db.GetAllProjects());
            Assert.Equal(copyBytes, File.ReadAllBytes(copyJson));
        }

        // A project.json that exists but is damaged, empty or id-less is somebody's project: refuse, keep
        // its bytes, add no row.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("this is not json")]
        [InlineData("{\"name\": \"NoId\", \"description\": \"lost its id\"}")]
        public void Unreadable_project_json_is_refused_and_left_byte_for_byte(string contents)
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            Directory.CreateDirectory(Path.Combine(_folder, ".claude"));
            string jsonPath = Path.Combine(_folder, ".claude", "project.json");
            File.WriteAllText(jsonPath, contents);
            byte[] before = File.ReadAllBytes(jsonPath);
            using var broker = new MessageBroker { ProjectService = service };

            var create = broker.CreateProject("TestC", null, "new-project-dialog", _folder);
            var reuse = broker.CreateProject("TestC", null, "new-project-dialog", _folder, allowReuseExisting: true);
            var open = broker.RegisterExistingProject(_folder, "new-project-dialog");

            Assert.False(create.Success);
            Assert.True(create.FolderOccupied);
            Assert.False(reuse.Success);
            Assert.False(open.Success);
            Assert.Equal(before, File.ReadAllBytes(jsonPath));
            Assert.Empty(db.GetAllProjects());
        }

        // project.json says one id, the folder's only row is another: refuse, never insert a second row.
        [Fact]
        public void Json_id_differing_from_the_folders_row_is_refused_without_a_second_row()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            var first = broker.CreateProject("Testing", "fixture", "test", _folder);
            Assert.True(first.Success, first.Error);
            string jsonPath = Path.Combine(_folder, ".claude", "project.json");
            string json = File.ReadAllText(jsonPath);
            Assert.Contains(first.ProjectId, json);
            File.WriteAllText(jsonPath, json.Replace(first.ProjectId, "deadbeef"));

            var reuse = broker.CreateProject("TestC", null, "new-project-dialog", _folder, allowReuseExisting: true);
            var open = broker.RegisterExistingProject(_folder, "new-project-dialog");

            Assert.False(reuse.Success);
            Assert.False(open.Success);
            Assert.Contains("deadbeef", open.Error);
            Assert.Contains(first.ProjectId, open.Error);
            Assert.Single(db.GetAllProjects());
        }

        // Two creates on one free folder. The seam holds each at the racy point (after the folder check,
        // before the first write) until both are there, or 2s pass. Without the create lock both pass the
        // check and both create; with it the second waits for the first and then sees the folder taken.
        [Fact]
        public async Task Concurrent_creates_on_one_folder_yield_exactly_one_project()
        {
            using var db = new ProjectDatabase();
            using var service = new ProjectService(db);
            using var broker = new MessageBroker { ProjectService = service };
            using var barrier = new Barrier(2);
            broker.TestHookAfterFolderCheck = () => barrier.SignalAndWait(TimeSpan.FromSeconds(2));
            var results = new CreateProjectResult[2];

            var t1 = Task.Run(() => results[0] = broker.CreateProject("A", null, "test", _folder));
            var t2 = Task.Run(() => results[1] = broker.CreateProject("B", null, "test", _folder));

            // Bounded wait, not a bare await: a deadlocked create fails the test instead of hanging the suite.
            var both = Task.WhenAll(t1, t2);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
            Assert.True(await Task.WhenAny(both, timeoutTask) == both, "creates did not finish");
            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Single(db.GetAllProjects());
        }
    }
}
