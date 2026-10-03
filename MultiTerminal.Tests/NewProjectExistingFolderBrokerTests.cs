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
    }
}
