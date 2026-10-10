using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// A project's Claude profile name (ticket c8ee0eec item 5, GitHub #21): the
    /// <c>projects.claude_profile</c> column, its project.json key, and the dropdown item list.
    ///
    /// <para>Storage facts run against a real temp SQLite DB and a real temp project folder.</para>
    ///
    /// <para>Not covered: EditProjectDialog and ProjectManagerDialog themselves (that the combo is
    /// filled, read on save, and that the dialogs call the setters). Those are WinForms wiring no fact
    /// here executes; they need a live check. Nothing here launches a terminal either.</para>
    /// </summary>
    public sealed class ClaudeProfileProjectStorageTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _projectDir;
        private ProjectDatabase _projectDb;
        private ProjectService _service;

        public ClaudeProfileProjectStorageTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_cprofile_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _projectDir = Path.Combine(Path.GetTempPath(), $"mt_cprofileproj_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_projectDir);
        }

        public void Dispose()
        {
            _service?.Dispose();   // disposes the shared ProjectDatabase too
            _projectDb?.Dispose(); // idempotent second dispose (satisfies CA2213 for the owned field)
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            foreach (var f in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
            if (Directory.Exists(_projectDir)) Directory.Delete(_projectDir, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        private void OpenDb()
        {
            _projectDb = new ProjectDatabase();
            _service = new ProjectService(_projectDb);
        }

        // Registers a project (DB row + .claude/project.json) the way the app does.
        private Project SeedProject(string claudeProfile = null)
        {
            if (_projectDb == null) OpenDb();
            var project = Project.Create("ProfileProj", _projectDir);
            project.ClaudeProfile = claudeProfile;
            _service.SaveProject(project);
            return project;
        }

        private string ProjectJsonText() =>
            File.ReadAllText(Path.Combine(_projectDir, ".claude", "project.json"));

        private int ClaudeProfileColumnCount()
        {
            using var conn = new SQLiteConnection($"Data Source={_testDbPath};Version=3;");
            conn.Open();
            using var cmd = new SQLiteCommand("PRAGMA table_info(projects)", conn);
            using var reader = cmd.ExecuteReader();
            int count = 0;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "claude_profile", StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        // ── Migration ───────────────────────────────────────────────────────

        [Fact]
        public void A_database_without_the_column_gains_it_and_a_second_open_changes_nothing()
        {
            // A projects table as it was before this ticket's column (the original seven columns), with a row.
            using (var conn = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                conn.Open();
                using var create = new SQLiteCommand(@"
                    CREATE TABLE projects (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT, path TEXT,
                        created_by TEXT, created_at DATETIME NOT NULL, updated_at DATETIME NOT NULL)", conn);
                create.ExecuteNonQuery();
                using var insert = new SQLiteCommand(
                    "INSERT INTO projects (id, name, created_at, updated_at) VALUES ('old-1', 'Old', @now, @now)", conn);
                insert.Parameters.AddWithValue("@now", DateTime.UtcNow);
                insert.ExecuteNonQuery();
            }
            SQLiteConnection.ClearAllPools();
            Assert.Equal(0, ClaudeProfileColumnCount()); // precondition: the column really is absent

            using (var first = new ProjectDatabase())
            {
                Assert.Null(first.GetRichProject("old-1").ClaudeProfile); // the existing row reads as Default
                Assert.True(first.SetClaudeProfile("old-1", "Work"));
            }
            SQLiteConnection.ClearAllPools();
            Assert.Equal(1, ClaudeProfileColumnCount());

            using (var second = new ProjectDatabase())
            {
                Assert.Equal("Work", second.GetRichProject("old-1").ClaudeProfile);
            }
            SQLiteConnection.ClearAllPools();
            Assert.Equal(1, ClaudeProfileColumnCount());
        }

        // ── Column read/write ───────────────────────────────────────────────

        [Fact]
        public void A_project_that_never_chose_a_profile_reads_as_default()
        {
            var project = SeedProject();

            Assert.Null(_projectDb.GetRichProject(project.Id).ClaudeProfile);
        }

        [Fact]
        public void A_new_project_saved_with_a_profile_reads_it_back_from_both_queries()
        {
            var project = SeedProject("Work");

            Assert.Equal("Work", _projectDb.GetRichProject(project.Id).ClaudeProfile);
            Assert.Equal("Work", _projectDb.GetAllRichProjects().Single(p => p.Id == project.Id).ClaudeProfile);
        }

        [Fact]
        public void SetClaudeProfile_sets_and_clears()
        {
            var project = SeedProject();

            Assert.True(_projectDb.SetClaudeProfile(project.Id, "Work"));
            Assert.Equal("Work", _projectDb.GetRichProject(project.Id).ClaudeProfile);

            Assert.True(_projectDb.SetClaudeProfile(project.Id, null));
            Assert.Null(_projectDb.GetRichProject(project.Id).ClaudeProfile);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Default")]
        [InlineData("default")]
        public void Blank_or_Default_is_stored_as_no_profile(string name)
        {
            var project = SeedProject("Work");

            Assert.True(_projectDb.SetClaudeProfile(project.Id, name));

            Assert.Null(_projectDb.GetRichProject(project.Id).ClaudeProfile);
        }

        [Fact]
        public void Setting_it_on_an_unknown_project_reports_failure()
        {
            OpenDb();

            Assert.False(_projectDb.SetClaudeProfile("no-such-project", "Work"));
        }

        [Fact]
        public void The_single_field_update_writes_and_clears_it()
        {
            // PATCH /api/projects/{id} sends the camelCase field name.
            var project = SeedProject();

            Assert.True(_projectDb.UpdateProjectField(project.Id, "claudeProfile", "Work"));
            Assert.Equal("Work", _projectDb.GetRichProject(project.Id).ClaudeProfile);

            Assert.True(_projectDb.UpdateProjectField(project.Id, "claudeProfile", ""));
            Assert.Null(_projectDb.GetRichProject(project.Id).ClaudeProfile);
        }

        // ── A stale project.json must not move a project to another profile ──

        [Fact]
        public void A_project_json_resave_does_not_bring_back_a_profile_that_was_changed()
        {
            // ChangelogService / VersioningService do LoadProject -> SaveProject. The file still names
            // the old profile, and that must not replace the one chosen since.
            var project = SeedProject("Work");
            Assert.True(_projectDb.SetClaudeProfile(project.Id, "Personal"));

            var fromJson = _service.LoadProject(_projectDir);
            Assert.Equal("Work", fromJson.ClaudeProfile); // precondition: the file really is stale
            _service.SaveProject(fromJson);

            Assert.Equal("Personal", _projectDb.GetRichProject(project.Id).ClaudeProfile);
        }

        [Fact]
        public void A_project_json_resave_does_not_bring_back_a_profile_that_was_cleared()
        {
            var project = SeedProject("Work");
            Assert.True(_projectDb.SetClaudeProfile(project.Id, null));

            var fromJson = _service.LoadProject(_projectDir);
            Assert.Equal("Work", fromJson.ClaudeProfile); // precondition: the file really is stale
            _service.SaveProject(fromJson);

            Assert.Null(_projectDb.GetRichProject(project.Id).ClaudeProfile);
        }

        // ── project.json ────────────────────────────────────────────────────

        [Fact]
        public void Project_json_round_trips_the_profile_name()
        {
            SeedProject("Work");

            Assert.Equal("Work", _service.LoadProject(_projectDir).ClaudeProfile);
        }

        [Fact]
        public void A_default_project_writes_no_key_and_a_file_without_the_key_reads_as_default()
        {
            SeedProject();

            Assert.DoesNotContain("claudeProfile", ProjectJsonText(), StringComparison.OrdinalIgnoreCase);
            Assert.Null(_service.LoadProject(_projectDir).ClaudeProfile);
        }

        [Fact]
        public void The_file_only_update_rewrites_the_key_and_leaves_the_database_alone()
        {
            var project = SeedProject("Work");

            Assert.True(_service.UpdateProjectJsonClaudeProfile(_projectDir, project.Id, "Personal"));
            Assert.Equal("Personal", _service.LoadProject(_projectDir).ClaudeProfile);
            Assert.Equal("Work", _projectDb.GetRichProject(project.Id).ClaudeProfile);

            Assert.True(_service.UpdateProjectJsonClaudeProfile(_projectDir, project.Id, null));
            Assert.Null(_service.LoadProject(_projectDir).ClaudeProfile);

            Assert.False(_service.UpdateProjectJsonClaudeProfile(_projectDir, "some-other-id", "Personal"));
            Assert.Null(_service.LoadProject(_projectDir).ClaudeProfile);
        }

        // ── Dropdown item list (pure) ───────────────────────────────────────

        private static readonly ClaudeProfile[] Configured =
        {
            new ClaudeProfile(ClaudeProfile.DefaultName, @"C:\Users\x\.claude"),
            new ClaudeProfile("Work", @"C:\profiles\work"),
            new ClaudeProfile("Personal", @"C:\profiles\personal"),
        };

        [Fact]
        public void The_list_is_Default_then_the_configured_profiles_and_Default_stores_null()
        {
            var choices = ClaudeProfileChoices.Build(Configured, null, out int selected);

            Assert.Equal(new[] { "Default", "Work", "Personal" }, choices.Select(c => c.Label));
            Assert.Equal(new[] { null, "Work", "Personal" }, choices.Select(c => c.Name));
            Assert.Equal(0, selected);
            Assert.DoesNotContain(choices, c => c.IsMissing);
        }

        [Fact]
        public void A_configured_stored_name_is_selected_ignoring_case_and_adds_no_entry()
        {
            var choices = ClaudeProfileChoices.Build(Configured, " personal ", out int selected);

            Assert.Equal(3, choices.Count);
            Assert.Equal("Personal", choices[selected].Name);
        }

        [Fact]
        public void A_stored_name_missing_on_this_machine_is_listed_selected_and_keeps_its_name()
        {
            var choices = ClaudeProfileChoices.Build(Configured, "Client-X", out int selected);

            Assert.Equal(4, choices.Count);
            var chosen = choices[selected];
            Assert.True(chosen.IsMissing);
            Assert.Equal("Client-X (not configured on this machine)", chosen.Label);
            Assert.Equal("Client-X", chosen.Name); // what an untouched save writes back
        }

        [Fact]
        public void With_no_profile_list_at_all_the_stored_name_is_still_kept()
        {
            var choices = ClaudeProfileChoices.Build(null, "Work", out int selected);

            Assert.Equal(new[] { null, "Work" }, choices.Select(c => c.Name));
            Assert.Equal("Work", choices[selected].Name);
            Assert.True(choices[selected].IsMissing);
        }
    }
}
