using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The per-project launch mode (task ec2ed636): <c>projects.launch_mode</c>, read as
    /// <see cref="ProjectLaunchMode"/> and written by <see cref="ProjectDatabase.SetLaunchMode"/>.
    ///
    /// <para>Storage only, against a real temp SQLite DB. Nothing here exercises a launch: what a
    /// launch does with the mode is another checklist item.</para>
    /// </summary>
    public sealed class ProjectLaunchModeTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly List<ProjectDatabase> _databases = new List<ProjectDatabase>();

        public ProjectLaunchModeTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_launchmode_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
        }

        public void Dispose()
        {
            foreach (var db in _databases) db.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            foreach (var file in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(file)) File.Delete(file);
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        private ProjectDatabase OpenDb()
        {
            var db = new ProjectDatabase();
            _databases.Add(db);
            return db;
        }

        private static Project SeedProject(ProjectDatabase db)
        {
            var project = Project.Create("LaunchModeProj", Path.Combine(Path.GetTempPath(), "mt_launchmode_unused"));
            db.SaveRichProject(project);
            return project;
        }

        private SQLiteConnection OpenRaw()
        {
            var conn = new SQLiteConnection($"Data Source={_testDbPath};Version=3;");
            conn.Open();
            return conn;
        }

        private object ReadRawLaunchMode(string projectId)
        {
            using var conn = OpenRaw();
            using var cmd = new SQLiteCommand("SELECT launch_mode FROM projects WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", projectId);
            return cmd.ExecuteScalar();
        }

        private void WriteRawLaunchMode(string projectId, string value)
        {
            using var conn = OpenRaw();
            using var cmd = new SQLiteCommand("UPDATE projects SET launch_mode = @v WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@v", value);
            cmd.Parameters.AddWithValue("@id", projectId);
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        private bool HasLaunchModeColumn()
        {
            using var conn = OpenRaw();
            using var cmd = new SQLiteCommand("PRAGMA table_info(projects)", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "launch_mode", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        [Fact]
        public void A_project_that_never_set_it_asks()
        {
            var db = OpenDb();
            var project = SeedProject(db);

            Assert.IsType<DBNull>(ReadRawLaunchMode(project.Id)); // precondition: really unset (NULL)
            Assert.Equal(ProjectLaunchMode.Ask, db.GetRichProject(project.Id).LaunchMode);
        }

        [Theory]
        [InlineData(ProjectLaunchMode.New, "new")]
        [InlineData(ProjectLaunchMode.Continue, "continue")]
        [InlineData(ProjectLaunchMode.Ask, "ask")]
        public void Each_mode_round_trips_as_its_lowercase_string(ProjectLaunchMode mode, string stored)
        {
            var db = OpenDb();
            var project = SeedProject(db);
            // Start from a different value, so Ask cannot pass merely by being the default.
            Assert.True(db.SetLaunchMode(project.Id,
                mode == ProjectLaunchMode.Continue ? ProjectLaunchMode.New : ProjectLaunchMode.Continue));

            Assert.True(db.SetLaunchMode(project.Id, mode));

            Assert.Equal(stored, ReadRawLaunchMode(project.Id));
            Assert.Equal(mode, db.GetRichProject(project.Id).LaunchMode);
            Assert.Equal(mode, Assert.Single(db.GetAllRichProjects()).LaunchMode);
        }

        [Theory]
        [InlineData("CONTINUE", ProjectLaunchMode.Continue)]
        [InlineData(" New ", ProjectLaunchMode.New)]
        [InlineData("resume", ProjectLaunchMode.Ask)]
        [InlineData("", ProjectLaunchMode.Ask)]
        [InlineData("2", ProjectLaunchMode.Ask)] // the enum's numeric value is not a storage form
        public void A_stored_string_parses_case_insensitively_and_unknown_reads_as_ask(string raw, ProjectLaunchMode expected)
        {
            var db = OpenDb();
            var project = SeedProject(db);

            WriteRawLaunchMode(project.Id, raw);

            Assert.Equal(expected, db.GetRichProject(project.Id).LaunchMode);
        }

        [Fact]
        public void A_rich_project_resave_does_not_reset_it()
        {
            // project.json does not carry the mode, so a re-save arrives with the property's default.
            var db = OpenDb();
            var project = SeedProject(db);
            Assert.True(db.SetLaunchMode(project.Id, ProjectLaunchMode.Continue));

            var resave = Project.Create(project.Name, project.Path);
            resave.Id = project.Id;
            Assert.Equal(ProjectLaunchMode.Ask, resave.LaunchMode); // precondition
            db.SaveRichProject(resave);

            Assert.Equal(ProjectLaunchMode.Continue, db.GetRichProject(project.Id).LaunchMode);
        }

        [Fact]
        public void Setting_it_on_an_unknown_project_reports_failure()
        {
            Assert.False(OpenDb().SetLaunchMode("no-such-project", ProjectLaunchMode.New));
        }

        [Fact]
        public void The_migration_is_idempotent_on_an_existing_db()
        {
            var first = OpenDb();
            var project = SeedProject(first);
            first.SetLaunchMode(project.Id, ProjectLaunchMode.New);

            // A second ProjectDatabase re-runs every migration against a DB that already has the
            // column; a non-idempotent ALTER would throw "duplicate column name" here.
            var second = OpenDb();

            Assert.Equal(ProjectLaunchMode.New, second.GetRichProject(project.Id).LaunchMode);
        }

        [Fact]
        public void A_db_that_predates_the_column_gains_it_and_its_projects_ask()
        {
            using (var conn = OpenRaw())
            {
                // The original seven-column table, with a row, and no launch_mode.
                using var create = new SQLiteCommand(@"
                    CREATE TABLE projects (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT, path TEXT,
                        created_by TEXT, created_at DATETIME NOT NULL, updated_at DATETIME NOT NULL);
                    INSERT INTO projects (id, name, created_at, updated_at)
                        VALUES ('legacy-1', 'Legacy', '2026-01-01 00:00:00', '2026-01-01 00:00:00');", conn);
                create.ExecuteNonQuery();
            }
            Assert.False(HasLaunchModeColumn()); // precondition: the DB really predates it

            var db = OpenDb();

            Assert.True(HasLaunchModeColumn());
            Assert.Equal(ProjectLaunchMode.Ask, db.GetRichProject("legacy-1").LaunchMode);
            Assert.True(db.SetLaunchMode("legacy-1", ProjectLaunchMode.Continue));
            Assert.Equal(ProjectLaunchMode.Continue, db.GetRichProject("legacy-1").LaunchMode);
        }
    }
}
