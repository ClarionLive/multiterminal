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
    /// <para>Not covered: the form itself (that it sends these fields), the broker's rename (the stub host
    /// below does nothing for it, so these facts show the service's own writes) and the gateway (only that
    /// the service asks for a sync, and with which names).</para>
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
            Assert.Empty(_host.Renames); // the name did not change, so the broker was not asked
            Assert.Equal(1, _host.NotifyCount);
        }

        /// <summary>
        /// The retired Project Manager edit re-saved the row from project.json, which has no gitAutoCommit,
        /// and so reset Auto-commit to off right after the dialog set it. A save that changes a project.json
        /// field must leave Auto-commit as saved. Falsified: routing SyncProjectJson through
        /// ProjectService.SaveProject(LoadProject(path)) turns the first assertion red.
        /// </summary>
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
        public void A_rename_goes_through_the_host_and_lands_in_the_row()
        {
            var p = Seed();
            var result = _service.Save(UpdateFor(p, ("name", "Renamed")));

            Assert.True(result.Success, result.Error);
            Assert.Equal(new[] { (p.Id, "Renamed", (string)null) }, _host.Renames);
            Assert.Equal("Renamed", _db.GetRichProject(p.Id).Name);
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

        private sealed class StubHost : IProjectSettingsHost
        {
            public List<(string Id, string Name, string Description)> Renames { get; } = new List<(string, string, string)>();
            public List<ISet<string>> GatewaySyncs { get; } = new List<ISet<string>>();
            public int NotifyCount { get; private set; }

            public string RenameProject(string projectId, string name, string description)
            {
                Renames.Add((projectId, name, description));
                return null;
            }

            public void NotifyProjectsChanged() => NotifyCount++;

            public void SyncGatewayProfile(string projectId, string projectName, ISet<string> enabledServerNames) =>
                GatewaySyncs.Add(enabledServerNames);
        }
    }
}
