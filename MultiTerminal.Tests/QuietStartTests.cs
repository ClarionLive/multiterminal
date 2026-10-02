using System;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.Models;
using MultiTerminal.Services;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The per-project "Quiet start" setting (GitHub #34, task e0fa9d90): storage, and the launch
    /// decision behind <c>MULTITERMINAL_QUIET_START</c>.
    ///
    /// <para>The storage facts run against a real temp SQLite DB. A fresh DB gets the column from
    /// the same idempotent migration list an existing DB does (CreateSchema makes only the original
    /// seven columns), so these facts exercise the migration. No fact here starts from a DB that
    /// predates the column, though, so "an upgraded DB reads off" holds by that shared path, not by
    /// a test of the upgrade itself.</para>
    ///
    /// <para>Not covered: that <c>ConPtyTerminal.StartProcess</c> appends the assignment, that
    /// TerminalDocument sets the flag before starting, and that MainForm and TerminalControl skip
    /// the typed kick. Each is one line no test here sees; they need a live check.</para>
    /// </summary>
    public sealed class QuietStartTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _projectDir;
        private readonly ProjectDatabase _projectDb;
        private readonly ProjectService _service;

        public QuietStartTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_quiet_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _projectDb = new ProjectDatabase();
            _service = new ProjectService(_projectDb);
            _projectDir = Path.Combine(Path.GetTempPath(), $"mt_quietproj_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_projectDir);
        }

        public void Dispose()
        {
            _service?.Dispose();   // disposes the shared ProjectDatabase too
            _projectDb?.Dispose(); // idempotent second dispose (satisfies CA2213 for the owned field)
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            foreach (var side in new[] { _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(side)) File.Delete(side);
            }
            if (Directory.Exists(_projectDir)) Directory.Delete(_projectDir, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        // Registers a project (DB + .claude/project.json) the way the app does, with no quiet-start value.
        private Project SeedProject()
        {
            var project = Project.Create("QuietProj", _projectDir);
            _service.SaveProject(project);
            return project;
        }

        [Fact]
        public void A_project_that_never_set_it_starts_normally()
        {
            // Owner decision: default OFF, so every existing project keeps today's startup.
            var project = SeedProject();

            var stored = _projectDb.GetRichProject(project.Id);
            Assert.Null(stored.QuietStart);
            Assert.False(stored.IsQuietStart);
        }

        [Fact]
        public void A_project_json_resave_does_not_turn_it_off()
        {
            // ChangelogService / VersioningService re-save from project.json, which does not carry the
            // field. Their null must not overwrite the user's choice (the COALESCE in SaveRichProject).
            var project = SeedProject();
            Assert.True(_projectDb.SetQuietStart(project.Id, true));

            var fromJson = _service.LoadProject(_projectDir);
            Assert.Null(fromJson.QuietStart); // precondition: the re-save really carries no value
            _service.SaveProject(fromJson);

            Assert.True(_projectDb.GetRichProject(project.Id).IsQuietStart);
        }

        [Fact]
        public void An_explicit_off_is_stored_as_off()
        {
            var project = SeedProject();
            _projectDb.SetQuietStart(project.Id, true);

            Assert.True(_projectDb.SetQuietStart(project.Id, false));

            Assert.False(_projectDb.GetRichProject(project.Id).QuietStart);
        }

        [Fact]
        public void The_project_panel_toggle_writes_it()
        {
            // panel.html sends the camelCase data-field and the strings 'true' / 'false'.
            var project = SeedProject();

            Assert.True(_projectDb.UpdateProjectField(project.Id, "quietStart", "true"));
            Assert.True(_projectDb.GetRichProject(project.Id).QuietStart);

            Assert.True(_projectDb.UpdateProjectField(project.Id, "quietStart", "false"));
            Assert.False(_projectDb.GetRichProject(project.Id).QuietStart);
        }

        [Fact]
        public void Setting_it_on_an_unknown_project_reports_failure()
        {
            Assert.False(_projectDb.SetQuietStart("no-such-project", true));
        }

        [Theory]
        [InlineData(true, null, true)]      // Owner opens a quiet project
        [InlineData(true, "", true)]        // an empty spawner is no spawner, as in TerminalRoles.Resolve
        [InlineData(true, "Alice", false)]  // a helper must get its first turn to collect its job
        [InlineData(true, "ClaudeRemote", false)]
        [InlineData(false, null, false)]    // the default
        [InlineData(false, "Alice", false)]
        public void Only_a_terminal_that_was_not_spawned_starts_quietly(bool projectQuietStart, string spawnerName, bool expected)
        {
            Assert.Equal(expected, TerminalRoles.IsQuietStart(projectQuietStart, spawnerName));
        }

        [Fact]
        public void The_launch_variable_is_set_for_a_quiet_start_and_cleared_otherwise()
        {
            // The clear form (not omission) is what stops a shell inside a quiet terminal from passing
            // the flag to a terminal that is not quiet. The hook accepts only the exact value 'true'.
            Assert.Equal("$env:MULTITERMINAL_QUIET_START = 'true'; ", ConPtyTerminal.BuildQuietStartEnvAssignment(true));
            Assert.Equal("$env:MULTITERMINAL_QUIET_START = $null; ", ConPtyTerminal.BuildQuietStartEnvAssignment(false));
        }
    }
}
