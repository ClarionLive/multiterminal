using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The project settings save path (ticket e20e2ce0), against a real temp SQLite DB and a real temp
    /// project folder with a .claude/project.json.
    ///
    /// <para>Not covered: the form itself (that it sends these fields), the broker's re-read after a rename
    /// (the stub host only records that it was asked) and the gateway (only that the service asks for a
    /// sync, and with which names).</para>
    /// </summary>
    public sealed class ProjectSettingsServiceTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _projectDir;
        private readonly ProjectDatabase _db;
        private readonly ProjectService _projectService;
        private readonly StubHost _host = new StubHost();
        private readonly ProjectSettingsService _service;

        public ProjectSettingsServiceTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_psettings_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _projectDir = Path.Combine(Path.GetTempPath(), $"mt_psettings_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_projectDir);
            _db = new ProjectDatabase();
            _projectService = new ProjectService(_db);
            _service = new ProjectSettingsService(_db, _projectService, _host);
        }

        public void Dispose()
        {
            _projectService.Dispose();
            _db.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var f in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }
            if (Directory.Exists(_projectDir)) Directory.Delete(_projectDir, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        private Project Seed(string claudeProfile = null)
        {
            var project = Project.Create("SettingsProj", _projectDir);
            project.ClaudeProfile = claudeProfile;
            _projectService.SaveProject(project);
            return project;
        }

        private ProjectSettingsUpdate UpdateFor(Project p, params (string Field, string Value)[] fields)
        {
            var u = new ProjectSettingsUpdate { ProjectId = p.Id };
            foreach (var (field, value) in fields) u.Fields[field] = value;
            return u;
        }

        private string ProjectJsonText() => File.ReadAllText(Path.Combine(_projectDir, ".claude", "project.json"));

        [Fact]
        public void Only_changed_fields_are_written_and_reported()
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p,
                ("buildCommand", "dotnet build"),
                ("projectType", "dotnet"),
                ("name", "SettingsProj"))); // unchanged

            Assert.True(result.Success, result.Error);
            Assert.Equal(new[] { "buildCommand", "projectType" }, result.ChangedFields.OrderBy(f => f, StringComparer.Ordinal));
            var stored = _db.GetRichProject(p.Id);
            Assert.Equal("dotnet build", stored.BuildCommand);
            Assert.Equal("dotnet", stored.ProjectType);
            Assert.Empty(_host.Renames); // the name did not change, so the host was not told of a rename
            Assert.Equal(1, _host.NotifyCount);
        }

        /// <summary>
        /// The form's "applies at next launch" message (item 2) follows what the save actually changed: a
        /// launch-time field says so, a field running terminals pick up at once does not.
        /// </summary>
        [Fact]
        public void Only_a_launch_time_change_is_reported_as_applying_at_next_launch()
        {
            var p = Seed();
            var buildOnly = _service.Save(UpdateFor(p, ("buildCommand", "dotnet build")));
            var quiet = _service.Save(UpdateFor(p, ("quietStart", "true")));

            Assert.True(buildOnly.Success, buildOnly.Error);
            Assert.True(quiet.Success, quiet.Error);
            Assert.False(buildOnly.AppliesAtNextLaunch);
            Assert.True(quiet.AppliesAtNextLaunch);
            Assert.Subset(
                new HashSet<string>(ProjectSettingsService.EditableFields, StringComparer.Ordinal),
                new HashSet<string>(ProjectSettingsSaveResult.LaunchTimeFields, StringComparer.Ordinal));
        }

        /// <summary>
        /// The retired Project Manager edit re-saved the row from project.json, which has no gitAutoCommit,
        /// and so reset Auto-commit to off right after the dialog set it. A save that changes a project.json
        /// field must leave Auto-commit as saved. Falsified: routing SyncProjectJson through
        /// ProjectService.SaveProject(LoadProject(path)) turns the first assertion red.
        /// </summary>
        [Fact]
        public void Auto_commit_survives_a_save_that_also_rewrites_project_json()
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p, ("gitAutoCommit", "true"), ("isPinned", "true")));

            Assert.True(result.Success, result.Error);
            Assert.True(_db.GetRichProject(p.Id).GitAutoCommit);
            Assert.True(_db.GetRichProject(p.Id).IsPinned);
            Assert.True(_projectService.LoadProject(_projectDir).IsPinned); // the file was written too
        }

        [Fact]
        public void Choosing_Default_clears_the_Claude_profile_in_the_row_and_the_file()
        {
            var p = Seed(claudeProfile: "Work");
            Assert.Contains("\"claudeProfile\"", ProjectJsonText()); // precondition: the file names it

            var result = _service.Save(UpdateFor(p, ("claudeProfile", "Default")));

            Assert.True(result.Success, result.Error);
            Assert.Null(_db.GetRichProject(p.Id).ClaudeProfile);
            Assert.DoesNotContain("\"claudeProfile\"", ProjectJsonText());
        }

        [Fact]
        public void A_field_the_form_may_not_write_rejects_the_whole_save()
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p, ("buildCommand", "make"), ("path", @"C:\elsewhere")));

            Assert.False(result.Success);
            Assert.Contains("path", result.Error);
            Assert.Null(_db.GetRichProject(p.Id).BuildCommand); // nothing before it was written
            Assert.Equal(0, _host.NotifyCount);
        }

        [Fact]
        public void A_blank_name_rejects_the_whole_save()
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p, ("deployCommand", "deploy.ps1"), ("name", "  ")));

            Assert.False(result.Success);
            Assert.Equal("SettingsProj", _db.GetRichProject(p.Id).Name);
            Assert.Null(_db.GetRichProject(p.Id).DeployCommand);
        }

        [Fact]
        public void A_rename_lands_in_the_row_and_the_host_is_told_the_previous_name()
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p, ("name", "Renamed")));

            Assert.True(result.Success, result.Error);
            Assert.Equal(new[] { (p.Id, "SettingsProj") }, _host.Renames);
            Assert.Equal("Renamed", _db.GetRichProject(p.Id).Name);
        }

        /// <summary>
        /// A rename is written inside the save's transaction, so the stale-form check covers it like any other
        /// field: renaming in the form after an agent renamed the project elsewhere is refused, and the agent's
        /// name stands. (Pipeline Run 2: the rename used to go through the broker after the commit, outside the
        /// check.) Falsified: skipping the baseline check in Save turned this red at the first assertion.
        /// </summary>
        [Fact]
        public void A_rename_that_collides_with_a_rename_made_elsewhere_is_refused()
        {
            var p = Seed();
            var loaded = Snapshot(p);
            _db.UpdateProjectField(p.Id, "name", "RenamedByAgent");

            var result = _service.Save(FormSave(loaded, ("name", "RenamedInForm")));

            Assert.False(result.Success);
            Assert.True(result.Conflict);
            Assert.Equal("RenamedByAgent", _db.GetRichProject(p.Id).Name);
            Assert.Empty(_host.Renames);
        }

        /// <summary>
        /// The broker's re-read after a rename runs once the save has committed. If it throws, the save is
        /// still a success (with a warning), and the steps after it still run: the change notification and the
        /// project.json sync. Falsified: removing the try/catch around ProjectRenamed in Save made the
        /// exception escape (red at the Save call).
        /// </summary>
        [Fact]
        public void A_failure_after_the_commit_is_a_warning_not_a_failed_save()
        {
            var p = Seed();
            _host.ThrowOnRename = new System.Data.SQLite.SQLiteException("database is locked");

            var result = _service.Save(UpdateFor(p, ("name", "Renamed"), ("currentVersion", "2.0.0")));

            Assert.True(result.Success, result.Error);
            Assert.Contains(result.Warnings, w => w.Contains("database is locked", StringComparison.Ordinal));
            Assert.Equal("Renamed", _db.GetRichProject(p.Id).Name);
            Assert.Equal("2.0.0", _projectService.LoadProject(_projectDir).CurrentVersion);
            Assert.Equal(1, _host.NotifyCount);
        }

        /// <summary>
        /// When the form could not load the project's lists, the page shows them empty and posts [] for each.
        /// That must not be read as "the user deleted every row". Falsified: letting ListEdit return the
        /// incoming list when the baseline list is null deleted the stored agent (red at the last assertion).
        /// </summary>
        [Fact]
        public void A_list_the_form_could_not_load_is_never_saved()
        {
            var p = Seed();
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "Kept", Role = "dev" });
            var loaded = Snapshot(p);
            loaded.Agents = null;   // as ProjectSettingsForm.SendLoad leaves it when GetProjectContext throws

            var u = FormSave(new ProjectSettingsBaseline
            {
                Project = loaded.Project, Agents = new List<ProjectAgent>(), SpecialistAgents = loaded.SpecialistAgents,
                McpServers = loaded.McpServers, Skills = loaded.Skills, Paths = loaded.Paths, Prompts = loaded.Prompts,
            }, ("buildCommand", "dotnet build"));
            u.Baseline = loaded;
            var result = _service.Save(u);

            Assert.True(result.Success, result.Error);
            Assert.Equal("dotnet build", _db.GetRichProject(p.Id).BuildCommand);
            Assert.Equal("Kept", _db.GetProjectAgents(p.Id).Single().AgentName);
        }

        /// <summary>
        /// A blank stored Version loads as "0.1.0". The stored side of the conflict check must be read the same
        /// way, or every Version edit on such a project is refused as "changed elsewhere". Falsified: comparing
        /// the raw stored value turned this red at the first assertion.
        /// </summary>
        [Fact]
        public void A_blank_stored_version_can_still_be_edited()
        {
            var p = Seed();
            _db.UpdateProjectField(p.Id, "currentVersion", "");
            var loaded = Snapshot(p);

            var result = _service.Save(FormSave(loaded, ("currentVersion", "1.0.0")));

            Assert.True(result.Success, result.Error);
            Assert.Equal("1.0.0", _db.GetRichProject(p.Id).CurrentVersion);
        }

        [Fact]
        public void Lists_are_diff_saved_and_a_foreign_row_id_is_never_touched()
        {
            var p = Seed();
            var other = Project.Create("Other", Path.Combine(_projectDir, "other"));
            _db.SaveRichProject(other);
            int foreignPathId = _db.SaveProjectPath(new ProjectPath { ProjectId = other.Id, PathType = "docs", PathValue = @"C:\theirs" });

            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "Old", Role = "dev" });
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "Kept", Role = "dev" });

            var u = UpdateFor(p);
            u.Agents = new List<ProjectAgent> { new ProjectAgent { AgentName = "Kept", Role = "lead" }, new ProjectAgent { AgentName = "New" } };
            // Claims the other project's row id: must be inserted as a new row for this project instead.
            u.Paths = new List<ProjectPath> { new ProjectPath { Id = foreignPathId, PathType = "docs", PathValue = @"C:\mine" } };

            var result = _service.Save(u);

            Assert.True(result.Success, result.Error);
            var agents = _db.GetProjectAgents(p.Id).ToDictionary(a => a.AgentName, a => a.Role);
            Assert.Equal(new[] { "Kept", "New" }, agents.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("lead", agents["Kept"]);
            Assert.Equal(@"C:\theirs", _db.GetProjectPaths(other.Id).Single().PathValue);
            Assert.Equal(@"C:\mine", _db.GetProjectPaths(p.Id).Single().PathValue);
        }

        /// <summary>
        /// What the form loads, saved back untouched, writes nothing. This is the form's real round trip
        /// (BuildLoad, then the page's save message, then Save) and covers the normalizations the two sides
        /// must agree on: status case, a stored Claude profile this machine lacks, null quiet start, blank
        /// optional text. Falsified: making ReadField return the raw nullable QuietStart (null, where the form
        /// sends "false") turned this red and nothing else. Returning the raw Status instead would NOT: status
        /// is compared ignoring case, so "Parked" and "parked" are already the same value to the service.
        /// </summary>
        [Fact]
        public void An_untouched_form_save_writes_nothing()
        {
            var p = Seed(claudeProfile: "NotOnThisMachine");
            _db.UpdateProjectField(p.Id, "status", "Parked");
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "Kept", Role = "dev" });
            var stored = _db.GetRichProject(p.Id);
            var lists = new ProjectContextService(_db).GetProjectContext(p.Id);

            string load = MultiTerminal.ProjectSettings.ProjectSettingsPayload.BuildLoad(
                stored, lists, new MultiTerminal.ProjectSettings.ProjectSettingsOptions(), isDark: true);
            using var loadDoc = System.Text.Json.JsonDocument.Parse(load);
            var save = new Dictionary<string, object>
            {
                ["type"] = "save",
                ["fields"] = loadDoc.RootElement.GetProperty("fields"),
                ["agents"] = loadDoc.RootElement.GetProperty("lists").GetProperty("agents"),
                ["mcpServers"] = loadDoc.RootElement.GetProperty("lists").GetProperty("mcpServers"),
            };
            using var saveDoc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(save));

            var result = _service.Save(MultiTerminal.ProjectSettings.ProjectSettingsPayload.ParseSave(p.Id, saveDoc.RootElement));

            Assert.True(result.Success, result.Error);
            Assert.Empty(result.ChangedFields);
            Assert.False(result.McpServersChanged);
            Assert.Equal("NotOnThisMachine", _db.GetRichProject(p.Id).ClaudeProfile);
            Assert.Equal("Kept", _db.GetProjectAgents(p.Id).Single().AgentName);
        }

        [Fact]
        public void The_gateway_is_synced_only_when_MCP_servers_change()
        {
            var p = Seed();
            var u = UpdateFor(p);
            u.McpServers = new List<ProjectMcpServer>
            {
                new ProjectMcpServer { ServerName = "on", IsEnabled = true },
                new ProjectMcpServer { ServerName = "off", IsEnabled = false },
            };
            Assert.True(_service.Save(u).Success);
            Assert.Single(_host.GatewaySyncs);
            Assert.Equal(new[] { "on" }, _host.GatewaySyncs[0].OrderBy(n => n, StringComparer.Ordinal));

            // The same list again changes nothing, so no second sync.
            var again = _service.Save(u);
            Assert.True(again.Success);
            Assert.False(again.McpServersChanged);
            Assert.Single(_host.GatewaySyncs);
        }

        // What the form holds after loading: the stored project and lists, as ProjectSettingsForm.SendLoad keeps them.
        private ProjectSettingsBaseline Snapshot(Project p)
        {
            var lists = new ProjectContextService(_db).GetProjectContext(p.Id);
            return new ProjectSettingsBaseline
            {
                Project = _db.GetRichProject(p.Id),
                Agents = lists.Agents, SpecialistAgents = lists.SpecialistAgents, McpServers = lists.McpServers,
                Skills = lists.Skills, Paths = lists.Paths, Prompts = lists.Prompts,
            };
        }

        // The page's save: every field and every list as loaded, with the user's edits applied on top.
        private static ProjectSettingsUpdate FormSave(ProjectSettingsBaseline b, params (string Field, string Value)[] edits)
        {
            var u = new ProjectSettingsUpdate
            {
                ProjectId = b.Project.Id,
                Baseline = b,
                Agents = b.Agents.Select(a => new ProjectAgent { AgentName = a.AgentName, Role = a.Role, PreferredModel = a.PreferredModel }).ToList(),
                SpecialistAgents = b.SpecialistAgents.ToList(),
                McpServers = b.McpServers.ToList(),
                Skills = b.Skills.ToList(),
                Paths = b.Paths.ToList(),
                Prompts = b.Prompts.ToList(),
            };
            foreach (var field in ProjectSettingsService.EditableFields)
                u.Fields[field] = ProjectSettingsService.ReadField(b.Project, field) ?? string.Empty;
            foreach (var (field, value) in edits)
                u.Fields[field] = value;
            return u;
        }

        /// <summary>
        /// The form is modeless and can stay open for hours while a task's completion writes the changelog and
        /// an agent adds a project agent over REST. Saving an unrelated edit must keep both: only what the user
        /// changed against what the form loaded is written. Falsified: skipping the baseline check in Save
        /// (the pre-fix two-way compare) turned this red at ChangedFields, which then also held changeLog:
        /// the stale copy was being written back.
        /// </summary>
        [Fact]
        public void A_stale_form_keeps_what_was_changed_elsewhere_after_it_loaded()
        {
            var p = Seed();
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "Kept", Role = "dev" });
            var loaded = Snapshot(p);

            _db.UpdateProjectField(p.Id, "changeLog", "v0.2.0: written by task completion");
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "AddedElsewhere", Role = "qa" });

            var result = _service.Save(FormSave(loaded, ("buildCommand", "dotnet build")));

            Assert.True(result.Success, result.Error);
            Assert.Equal(new[] { "buildCommand" }, result.ChangedFields);
            var stored = _db.GetRichProject(p.Id);
            Assert.Equal("dotnet build", stored.BuildCommand);
            Assert.Equal("v0.2.0: written by task completion", stored.ChangeLog);
            Assert.Equal(new[] { "AddedElsewhere", "Kept" }, _db.GetProjectAgents(p.Id).Select(a => a.AgentName).OrderBy(n => n, StringComparer.Ordinal));
        }

        /// <summary>
        /// When the user edited a setting that was ALSO changed elsewhere after the form loaded, neither copy
        /// can be assumed right: the save is refused and writes nothing, not even the user's other edits.
        /// Falsified: skipping the baseline check in Save turned this red at the first assertion (the save
        /// succeeded and overwrote "elsewhere").
        /// </summary>
        [Fact]
        public void An_edit_that_collides_with_a_change_made_elsewhere_is_refused_and_writes_nothing()
        {
            var p = Seed();
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "Kept", Role = "dev" });
            var loaded = Snapshot(p);

            _db.UpdateProjectField(p.Id, "buildCommand", "elsewhere");
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "AddedElsewhere" });

            var u = FormSave(loaded, ("buildCommand", "mine"), ("deployCommand", "deploy.ps1"));
            u.Agents.Add(new ProjectAgent { AgentName = "AddedHere" });
            var result = _service.Save(u);

            Assert.False(result.Success);
            Assert.True(result.Conflict);
            Assert.Contains("buildCommand", result.Error);
            Assert.Contains("agents", result.Error);
            var stored = _db.GetRichProject(p.Id);
            Assert.Equal("elsewhere", stored.BuildCommand);
            Assert.Null(stored.DeployCommand);
            Assert.Equal(new[] { "AddedElsewhere", "Kept" }, _db.GetProjectAgents(p.Id).Select(a => a.AgentName).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(0, _host.NotifyCount);
        }

        /// <summary>
        /// A save that fails partway leaves the project exactly as it was: the scalars and the lists written
        /// before the failure are rolled back with it. The failure is real, not simulated: a trigger makes the
        /// database refuse the prompt insert, which runs after the scalars, agents and skills are written.
        /// Falsified: taking the BeginTransaction/Commit out of ProjectDatabase.RunInTransaction turned this red
        /// at the buildCommand assertion ("dotnet build" had been written before the prompt insert failed).
        /// </summary>
        [Fact]
        public void A_save_that_fails_partway_writes_nothing()
        {
            var p = Seed();
            _db.SaveProjectAgent(new ProjectAgent { ProjectId = p.Id, AgentName = "Kept", Role = "dev" });
            using (var raw = new SQLiteConnection($"Data Source={_testDbPath}"))
            {
                raw.Open();
                using var trigger = new SQLiteCommand(
                    "CREATE TRIGGER refuse_prompts BEFORE INSERT ON project_prompts BEGIN SELECT RAISE(ABORT, 'refused by test'); END;", raw);
                trigger.ExecuteNonQuery();
            }

            var u = UpdateFor(p, ("buildCommand", "dotnet build"));
            u.Agents = new List<ProjectAgent> { new ProjectAgent { AgentName = "Replacement" } };
            u.Prompts = new List<ProjectPromptEntry> { new ProjectPromptEntry { PromptType = "start", PromptText = "hello" } };
            var result = _service.Save(u);

            Assert.False(result.Success);
            Assert.Contains("Nothing was saved", result.Error);
            Assert.Null(_db.GetRichProject(p.Id).BuildCommand);
            Assert.Equal("Kept", _db.GetProjectAgents(p.Id).Single().AgentName);
            Assert.Empty(_db.GetProjectPrompts(p.Id));
        }

        // The raw stored launch_mode, so a test can tell the explicit 'ask' SetLaunchMode writes from NULL.
        private string RawLaunchMode(string projectId)
        {
            using var raw = new SQLiteConnection($"Data Source={_testDbPath}");
            raw.Open();
            using var cmd = new SQLiteCommand("SELECT launch_mode FROM projects WHERE id = @id", raw);
            cmd.Parameters.AddWithValue("@id", projectId);
            var value = cmd.ExecuteScalar();
            return value == null || value is DBNull ? null : (string)value;
        }

        /// <summary>
        /// The Launch mode field (ec2ed636 + e20e2ce0) saves through ProjectDatabase.SetLaunchMode, the
        /// column's only writer, and says it applies at the next launch. Choosing Ask stores the explicit
        /// 'ask' SetLaunchMode writes, not NULL. Falsified: sending launchMode through UpdateProjectField like
        /// the other scalars made the save fail ("Saving 'launchMode' failed", red at the first assertion),
        /// because UpdateProjectField does not whitelist launch_mode.
        /// </summary>
        [Fact]
        public void Launch_mode_saves_through_SetLaunchMode_and_applies_at_next_launch()
        {
            var p = Seed();

            var toContinue = _service.Save(UpdateFor(p, ("launchMode", "continue")));
            Assert.True(toContinue.Success, toContinue.Error);
            Assert.True(toContinue.AppliesAtNextLaunch);
            Assert.Equal(ProjectLaunchMode.Continue, _db.GetRichProject(p.Id).LaunchMode);

            var backToAsk = _service.Save(UpdateFor(p, ("launchMode", "ask")));
            Assert.True(backToAsk.Success, backToAsk.Error);
            Assert.Equal(ProjectLaunchMode.Ask, _db.GetRichProject(p.Id).LaunchMode);
            Assert.Equal("ask", RawLaunchMode(p.Id));
        }

        /// <summary>
        /// The launch mode is written inside the save's transaction, so a save that fails later rolls it back
        /// with everything else. Falsified: taking the BeginTransaction/Commit out of
        /// ProjectDatabase.RunInTransaction left launch_mode at 'new' (red at the LaunchMode assertion).
        /// </summary>
        [Fact]
        public void A_failed_save_rolls_back_the_launch_mode_too()
        {
            var p = Seed();
            using (var raw = new SQLiteConnection($"Data Source={_testDbPath}"))
            {
                raw.Open();
                using var trigger = new SQLiteCommand(
                    "CREATE TRIGGER refuse_prompts BEFORE INSERT ON project_prompts BEGIN SELECT RAISE(ABORT, 'refused by test'); END;", raw);
                trigger.ExecuteNonQuery();
            }

            var u = UpdateFor(p, ("launchMode", "new"));
            u.Prompts = new List<ProjectPromptEntry> { new ProjectPromptEntry { PromptType = "start", PromptText = "hello" } };
            var result = _service.Save(u);

            Assert.False(result.Success);
            Assert.Equal(ProjectLaunchMode.Ask, _db.GetRichProject(p.Id).LaunchMode);
            Assert.Null(RawLaunchMode(p.Id));
        }

        [Theory]
        [InlineData("continue", "continue")]
        [InlineData("NEW", "new")]
        [InlineData("", "ask")]
        [InlineData("sometimes", "ask")]
        public void An_unknown_launch_mode_reads_as_ask(string sent, string normalized)
        {
            Assert.Equal(normalized, ProjectSettingsService.NormalizeValue("launchMode", sent));
        }

        // ── Repository watch (ticket df58b35d, GitHub #53) ──────────────────────────

        [Fact]
        public void Repository_watch_settings_are_null_on_a_new_project_and_read_as_off_15_and_every_remote()
        {
            var p = Seed();
            var stored = _db.GetRichProject(p.Id);

            Assert.Null(stored.RepoWatchEnabled);
            Assert.Null(stored.RepoWatchIntervalMin);
            Assert.Null(stored.RepoWatchRepos);
            Assert.False(stored.IsRepoWatchEnabled);
            Assert.Equal(15, stored.RepoWatchIntervalMinutes);
            Assert.Equal("false", ProjectSettingsService.ReadField(stored, "repoWatchEnabled"));
            Assert.Equal("15", ProjectSettingsService.ReadField(stored, "repoWatchIntervalMin"));
        }

        [Fact]
        public void Repository_watch_settings_round_trip_through_save_and_reload()
        {
            var p = Seed();

            var on = _service.Save(UpdateFor(p,
                ("repoWatchEnabled", "true"),
                ("repoWatchIntervalMin", "0"),
                ("repoWatchRepos", " ClarionLive/multiterminal  msarson/Other,clarionlive/MULTITERMINAL ")));

            Assert.True(on.Success, on.Error);
            Assert.False(on.AppliesAtNextLaunch);
            var stored = _db.GetRichProject(p.Id);
            Assert.True(stored.RepoWatchEnabled);
            Assert.Equal(0, stored.RepoWatchIntervalMin);
            Assert.Equal("ClarionLive/multiterminal, msarson/Other", stored.RepoWatchRepos);
            Assert.Single(_db.GetAllRichProjects(), x => x.Id == p.Id && x.RepoWatchIntervalMin == 0 && x.RepoWatchEnabled == true);

            var off = _service.Save(UpdateFor(p,
                ("repoWatchEnabled", "false"),
                ("repoWatchIntervalMin", "60"),
                ("repoWatchRepos", "")));

            Assert.True(off.Success, off.Error);
            stored = _db.GetRichProject(p.Id);
            Assert.False(stored.RepoWatchEnabled);   // an explicit off, not NULL
            Assert.Equal(60, stored.RepoWatchIntervalMin);
            Assert.Null(stored.RepoWatchRepos);
        }

        [Theory]
        [InlineData("repoWatchIntervalMin", "7", "0, 5, 15, 30, 60")]
        [InlineData("repoWatchRepos", "good/one, a/b/c", "'a/b/c'")]
        [InlineData("repoWatchRepos", "-bad/x", "'-bad/x'")]
        [InlineData("repoWatchRepos", "https://github.com/o/r", "'https://github.com/o/r'")]
        public void A_bad_repository_watch_value_rejects_the_whole_save_with_a_message(string field, string value, string inMessage)
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p, ("buildCommand", "dotnet build"), (field, value)));

            Assert.False(result.Success);
            Assert.False(result.Conflict);
            Assert.Contains(inMessage, result.Error, StringComparison.Ordinal);
            var stored = _db.GetRichProject(p.Id);
            Assert.Null(stored.BuildCommand);
            Assert.Null(stored.RepoWatchIntervalMin);
            Assert.Null(stored.RepoWatchRepos);
            Assert.Equal(0, _host.NotifyCount);
        }

        /// <summary>
        /// update_project (REST PATCH) writes through UpdateProjectField with no settings-service validation in
        /// front of it, so the single-field writer refuses a bad value itself and stores a good one normalized.
        /// Falsified: skipping the interval branch in UpdateProjectField stored "7" (red at the first assertion).
        /// </summary>
        [Fact]
        public void The_single_field_writer_refuses_a_bad_value_and_normalizes_a_good_one()
        {
            var p = Seed();

            Assert.False(_db.UpdateProjectField(p.Id, "repoWatchIntervalMin", "7"));
            Assert.False(_db.UpdateProjectField(p.Id, "repoWatchRepos", "a/b/c"));
            Assert.Null(_db.GetRichProject(p.Id).RepoWatchIntervalMin);
            Assert.Null(_db.GetRichProject(p.Id).RepoWatchRepos);

            Assert.True(_db.UpdateProjectField(p.Id, "repoWatchIntervalMin", "30"));
            Assert.True(_db.UpdateProjectField(p.Id, "repoWatchRepos", "a/b  A/B c/d"));
            Assert.True(_db.UpdateProjectField(p.Id, "repoWatchEnabled", "true"));
            var stored = _db.GetRichProject(p.Id);
            Assert.Equal(30, stored.RepoWatchIntervalMin);
            Assert.Equal("a/b, c/d", stored.RepoWatchRepos);
            Assert.True(stored.RepoWatchEnabled);

            Assert.True(_db.UpdateProjectField(p.Id, "repoWatchIntervalMin", ""));
            Assert.Null(_db.GetRichProject(p.Id).RepoWatchIntervalMin);
        }

        /// <summary>
        /// The three settings are SQLite-only: the save does not write them to project.json, and a re-save of
        /// the row from project.json (what ChangelogService and VersioningService do) leaves them as they were.
        /// Falsified twice: writing a "repoWatchRepos" key in ProjectService.SerializeProjectJson turned the
        /// DoesNotContain red, and setting the three columns to NULL in SaveRichProject's ON CONFLICT branch
        /// turned the RepoWatchEnabled assertion after the re-save red.
        /// </summary>
        [Fact]
        public void Repository_watch_settings_are_not_in_project_json_and_survive_a_resave_from_it()
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p,
                ("repoWatchEnabled", "true"),
                ("repoWatchIntervalMin", "5"),
                ("repoWatchRepos", "o/r"),
                ("isPinned", "true")));   // a project.json field, so the file IS rewritten by this save
            Assert.True(result.Success, result.Error);
            Assert.True(_projectService.LoadProject(_projectDir).IsPinned); // precondition: the file was written

            string json = ProjectJsonText();
            Assert.DoesNotContain("repoWatch", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("repo_watch", json, StringComparison.OrdinalIgnoreCase);

            var fromJson = _projectService.LoadProject(_projectDir);
            Assert.Null(fromJson.RepoWatchEnabled);   // precondition: the re-save really carries no values
            _projectService.SaveProject(fromJson);

            var stored = _db.GetRichProject(p.Id);
            Assert.True(stored.RepoWatchEnabled);
            Assert.Equal(5, stored.RepoWatchIntervalMin);
            Assert.Equal("o/r", stored.RepoWatchRepos);
        }

        /// <summary>
        /// The form's round trip with repository watch values stored: loaded, saved back untouched, nothing is
        /// written. (The NULL case is <see cref="An_untouched_form_save_writes_nothing"/>.)
        /// </summary>
        [Fact]
        public void An_untouched_form_save_with_repository_watch_values_writes_nothing()
        {
            var p = Seed();
            _db.UpdateProjectField(p.Id, "repoWatchEnabled", "true");
            _db.UpdateProjectField(p.Id, "repoWatchIntervalMin", "30");
            _db.UpdateProjectField(p.Id, "repoWatchRepos", "o/r, p/q");
            var loaded = Snapshot(p);

            var result = _service.Save(FormSave(loaded));

            Assert.True(result.Success, result.Error);
            Assert.Empty(result.ChangedFields);
        }

        private sealed class StubHost : IProjectSettingsHost
        {
            public List<(string Id, string PreviousName)> Renames { get; } = new List<(string, string)>();
            public List<ISet<string>> GatewaySyncs { get; } = new List<ISet<string>>();
            public int NotifyCount { get; private set; }

            public Exception ThrowOnRename { get; set; }

            public void ProjectRenamed(string projectId, string previousName, string committedName)
            {
                Renames.Add((projectId, previousName));
                if (ThrowOnRename != null) throw ThrowOnRename;
            }

            public void NotifyProjectsChanged() => NotifyCount++;

            public void SyncGatewayProfile(string projectId, string projectName, ISet<string> enabledServerNames) =>
                GatewaySyncs.Add(enabledServerNames);
        }
    }
}
