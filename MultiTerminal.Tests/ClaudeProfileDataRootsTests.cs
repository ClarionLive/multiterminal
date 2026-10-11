using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec items 10 and 11: session data is read from, and written to, the config folder
    /// of the right Claude profile. Every test runs against real temp folders: a "home" whose
    /// <c>.claude</c> is the Default profile, a second configured profile ("Work"), a configured
    /// profile whose folder does not exist ("Gone"), and project folders that the fake project
    /// registry assigns to one of them. Nothing here reads the machine's settings or real profile
    /// folders: the resolver context and the project lookup are both injected.
    /// </summary>
    public sealed class ClaudeProfileDataRootsTests : IDisposable
    {
        private readonly string _base;
        private readonly string _defaultDir;
        private readonly string _workDir;
        private readonly string _goneDir;
        private readonly string _workProject;
        private readonly string _unownedProject;
        private readonly string _nopeProject;
        private readonly string _goneProject;
        private readonly ClaudeProfileContext _ctx;
        private readonly ClaudeProfileLookup _lookup;

        public ClaudeProfileDataRootsTests()
        {
            _base = Path.Combine(Path.GetTempPath(), $"mt_cpdr_{Guid.NewGuid():N}");
            string home = Path.Combine(_base, "home");
            _defaultDir = Path.Combine(home, ".claude");
            _workDir = Path.Combine(_base, "work-cfg");
            _goneDir = Path.Combine(_base, "gone-cfg");
            _workProject = Path.Combine(_base, "proj-work");
            _unownedProject = Path.Combine(_base, "proj-unowned");
            _nopeProject = Path.Combine(_base, "proj-nope");
            _goneProject = Path.Combine(_base, "proj-gone");

            foreach (string dir in new[] { _defaultDir, _workDir, _workProject, _unownedProject, _nopeProject, _goneProject })
                Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(_defaultDir, "projects"));
            Directory.CreateDirectory(Path.Combine(_workDir, "projects"));

            _ctx = new ClaudeProfileContext(
                new[] { new ClaudeProfile("Work", _workDir), new ClaudeProfile("Gone", _goneDir) },
                null,
                home,
                Directory.Exists);

            var rows = new[]
            {
                new ClaudeProfileProjectRow("p-work", "WorkProj", _workProject, null, "Work"),
                new ClaudeProfileProjectRow("p-nope", "NopeProj", _nopeProject, null, "Nope"),
                new ClaudeProfileProjectRow("p-gone", "GoneProj", _goneProject, null, "Gone"),
            };
            _lookup = new ClaudeProfileLookup(
                RowById: id => rows.FirstOrDefault(r => r.Id == id),
                AllRows: () => rows,
                LoadProjectFile: _ => null,
                CandidateFolders: ClaudeProfileLaunchPolicy.CandidateFolders);
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        }

        private ClaudeDataRoot RootFor(string folder) => ClaudeProfileDataRoots.ForProject(null, folder, _lookup, _ctx);

        /// <summary>Creates <c>&lt;configDir&gt;\projects\&lt;slug of projectPath&gt;</c> and returns it.</summary>
        private static string MakeClaudeProjectFolder(string configDir, string projectPath)
        {
            string dir = Path.Combine(configDir, "projects", SessionDiscovery.GetProjectFolderName(projectPath));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteSession(string claudeProjectFolder, string sessionId, DateTime modifiedUtc)
        {
            string path = Path.Combine(claudeProjectFolder, sessionId + ".jsonl");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                type = "user",
                uuid = Guid.NewGuid().ToString(),
                timestamp = "2026-10-10T10:00:00Z",
                message = new { role = "user", content = "hello" }
            }) + Environment.NewLine);
            File.SetLastWriteTimeUtc(path, modifiedUtc);
            return path;
        }

        private SessionDiscovery Discovery() => new SessionDiscovery(
            () => ClaudeProfileDataRoots.AllExisting(_ctx),
            RootFor,
            _ => "Alice");

        // ── Which folders ────────────────────────────────────────────────────

        [Fact]
        public void AllExisting_is_Default_then_each_configured_profile_whose_folder_exists()
        {
            var roots = ClaudeProfileDataRoots.AllExisting(_ctx);

            Assert.Equal(
                new[] { ("Default", _defaultDir), ("Work", _workDir) },
                roots.Select(r => (r.ProfileName, r.ConfigDir)).ToArray());
        }

        [Fact]
        public void A_pane_profile_name_maps_to_that_profiles_folder_and_an_unusable_one_to_nothing()
        {
            Assert.Equal(_defaultDir, ClaudeProfileDataRoots.ForProfileName(null, _ctx)?.ConfigDir);
            Assert.Equal(_workDir, ClaudeProfileDataRoots.ForProfileName("Work", _ctx)?.ConfigDir);
            Assert.Null(ClaudeProfileDataRoots.ForProfileName("Gone", _ctx));
            Assert.Null(ClaudeProfileDataRoots.ForProfileName("NotConfigured", _ctx));
        }

        [Fact]
        public void A_folder_maps_to_its_projects_profile_and_never_to_Default_when_that_profile_is_unusable()
        {
            Assert.Equal("Work", RootFor(_workProject)?.ProfileName);
            Assert.Equal(_workDir, RootFor(_workProject)?.ConfigDir);

            // A subfolder belongs to the project above it.
            string sub = Path.Combine(_workProject, "src");
            Directory.CreateDirectory(sub);
            Assert.Equal(_workDir, RootFor(sub)?.ConfigDir);

            // No project owns it: Default, as for a launch there.
            Assert.Equal(_defaultDir, RootFor(_unownedProject)?.ConfigDir);

            // The project's profile is not configured / has no folder: nothing, not Default.
            Assert.Null(RootFor(_nopeProject));
            Assert.Null(RootFor(_goneProject));
        }

        [Fact]
        public void With_no_project_registry_a_folder_maps_to_nothing()
        {
            Assert.Null(ClaudeProfileDataRoots.ForProject(null, _unownedProject, null, _ctx));
        }

        // ── SessionLineageService.GetClaudeProjectFolder ─────────────────────

        [Fact]
        public void GetClaudeProjectFolder_for_a_project_on_a_second_profile_is_the_folder_under_that_profile()
        {
            // The same project slug exists under BOTH profiles, so an answer that ignores the
            // project's profile has somewhere wrong to land.
            MakeClaudeProjectFolder(_defaultDir, _workProject);
            string underWork = MakeClaudeProjectFolder(_workDir, _workProject);

            Assert.Equal(underWork, SessionLineageService.GetClaudeProjectFolder(_workProject, _lookup, _ctx));
        }

        [Fact]
        public void GetClaudeProjectFolder_is_null_when_the_projects_profile_is_not_configured_even_if_Default_has_the_folder()
        {
            MakeClaudeProjectFolder(_defaultDir, _nopeProject);

            Assert.Null(SessionLineageService.GetClaudeProjectFolder(_nopeProject, _lookup, _ctx));
        }

        [Fact]
        public void GetClaudeProjectFolderForProfile_looks_only_under_the_panes_profile()
        {
            // One working folder, a Claude project folder for it under each profile.
            string underDefault = MakeClaudeProjectFolder(_defaultDir, _unownedProject);
            string underWork = MakeClaudeProjectFolder(_workDir, _unownedProject);

            Assert.Equal(underWork, SessionLineageService.GetClaudeProjectFolderForProfile(_unownedProject, "Work", _ctx));
            Assert.Equal(underDefault, SessionLineageService.GetClaudeProjectFolderForProfile(_unownedProject, null, _ctx));
        }

        [Fact]
        public void GetClaudeProjectFolderForProfile_does_not_find_another_profiles_folder()
        {
            // Only the Work profile has sessions for this folder. A Default pane must not see them.
            MakeClaudeProjectFolder(_workDir, _unownedProject);

            Assert.Null(SessionLineageService.GetClaudeProjectFolderForProfile(_unownedProject, null, _ctx));
            Assert.NotNull(SessionLineageService.GetClaudeProjectFolderForProfile(_unownedProject, "Work", _ctx));
        }

        // ── SessionSyncService ───────────────────────────────────────────────

        [Fact]
        public void SessionSyncService_finds_a_projects_folder_under_its_second_profile_only()
        {
            MakeClaudeProjectFolder(_defaultDir, _workProject);
            string underWork = MakeClaudeProjectFolder(_workDir, _workProject);
            MakeClaudeProjectFolder(_defaultDir, _nopeProject);

            var sync = new SessionSyncService(p => RootFor(p)?.ProjectsDir);

            Assert.Equal(underWork, sync.GetClaudeProjectPath(_workProject));
            Assert.Null(sync.GetClaudeProjectPath(_nopeProject));
        }

        // ── SessionDiscovery ─────────────────────────────────────────────────

        [Fact]
        public void Discovery_without_a_project_finds_a_session_under_a_second_profile_and_names_the_profile()
        {
            string sid = Guid.NewGuid().ToString();
            WriteSession(MakeClaudeProjectFolder(_workDir, _workProject), sid, DateTime.UtcNow.AddHours(-1));

            var found = Discovery().DiscoverAllSessionsForIdentity("Alice");

            var only = Assert.Single(found);
            Assert.Equal(sid, only.SessionId);
            Assert.Equal("Work", only.ClaudeProfile);
        }

        [Fact]
        public void Discovery_without_a_project_keeps_one_row_per_session_id_the_newer_transcript()
        {
            // Two ids, each present under both profiles, with the newer copy on OPPOSITE sides: an
            // implementation that keeps the first root, or the last, gets exactly one of them wrong.
            string newerInDefault = Guid.NewGuid().ToString();
            string newerInWork = Guid.NewGuid().ToString();
            string underDefault = MakeClaudeProjectFolder(_defaultDir, _unownedProject);
            string underWork = MakeClaudeProjectFolder(_workDir, _unownedProject);
            var old = DateTime.UtcNow.AddHours(-5);
            var recent = DateTime.UtcNow.AddHours(-1);
            WriteSession(underDefault, newerInDefault, recent);
            WriteSession(underWork, newerInDefault, old);
            WriteSession(underDefault, newerInWork, old);
            WriteSession(underWork, newerInWork, recent);

            var found = Discovery().DiscoverAllSessionsForIdentity("Alice");

            Assert.Equal(2, found.Count);
            Assert.Equal("Default", found.Single(s => s.SessionId == newerInDefault).ClaudeProfile);
            Assert.Equal("Work", found.Single(s => s.SessionId == newerInWork).ClaudeProfile);
        }

        [Fact]
        public void Discovery_for_a_project_reads_only_that_projects_profile()
        {
            string inDefault = Guid.NewGuid().ToString();
            string inWork = Guid.NewGuid().ToString();
            WriteSession(MakeClaudeProjectFolder(_defaultDir, _workProject), inDefault, DateTime.UtcNow.AddHours(-1));
            WriteSession(MakeClaudeProjectFolder(_workDir, _workProject), inWork, DateTime.UtcNow.AddHours(-2));

            var found = Discovery().DiscoverAllSessionsInProject(_workProject);

            var only = Assert.Single(found);
            Assert.Equal(inWork, only.SessionId);
            Assert.Equal("Work", only.ClaudeProfile);
        }

        [Fact]
        public void Discovery_for_a_project_whose_profile_is_not_configured_reads_nothing()
        {
            WriteSession(MakeClaudeProjectFolder(_defaultDir, _nopeProject), Guid.NewGuid().ToString(), DateTime.UtcNow.AddHours(-1));

            Assert.Empty(Discovery().DiscoverAllSessionsInProject(_nopeProject));
        }

        // ── SessionContextWriter ─────────────────────────────────────────────

        [Fact]
        public void Context_file_for_a_project_is_inside_that_projects_profile_folder()
        {
            string file = SessionContextWriter.ResolveContextFile(_workProject, RootFor, out string refusal);

            Assert.Null(refusal);
            Assert.Equal(
                Path.Combine(_workDir, "projects", SessionDiscovery.GetProjectFolderName(_workProject), "memory", "ACTIVE-CONTEXT.md"),
                file);
        }

        [Fact]
        public void Context_file_is_refused_for_an_unconfigured_or_missing_profile_and_no_folder_is_created()
        {
            Assert.Null(SessionContextWriter.ResolveContextFile(_nopeProject, RootFor, out string notConfigured));
            Assert.NotNull(notConfigured);

            Assert.Null(SessionContextWriter.ResolveContextFile(_goneProject, RootFor, out string missingFolder));
            Assert.NotNull(missingFolder);
            Assert.False(Directory.Exists(_goneDir));
        }

        // ── Path guard and .claude.json ──────────────────────────────────────

        [Fact]
        public void Path_guard_accepts_any_configured_profiles_projects_folder_and_nothing_beside_it()
        {
            var roots = ClaudeProfileResolver.GetAllRoots(_ctx);
            string inWork = Path.Combine(_workDir, "projects", "slug", "a.jsonl");

            Assert.True(ClaudeProfileDataRoots.IsUnderAnyRoot(inWork, "projects", roots, out string full));
            Assert.Equal(inWork, full);
            Assert.True(ClaudeProfileDataRoots.IsUnderAnyRoot(Path.Combine(_defaultDir, "projects", "slug", "a.jsonl"), "projects", roots, out _));

            // A sibling whose name merely starts with "projects", a folder that is not a profile,
            // a traversal out of the root, and a relative path.
            Assert.False(ClaudeProfileDataRoots.IsUnderAnyRoot(Path.Combine(_workDir, "projectsX", "a.jsonl"), "projects", roots, out _));
            Assert.False(ClaudeProfileDataRoots.IsUnderAnyRoot(Path.Combine(_base, "elsewhere", "projects", "a.jsonl"), "projects", roots, out _));
            Assert.False(ClaudeProfileDataRoots.IsUnderAnyRoot(Path.Combine(_workDir, "projects", "..", "..", "x.jsonl"), "projects", roots, out _));
            Assert.False(ClaudeProfileDataRoots.IsUnderAnyRoot(Path.Combine("projects", "a.jsonl"), "projects", roots, out _));
        }

        [Fact]
        public void Default_claude_json_is_beside_the_home_claude_folder_or_inside_an_inherited_config_dir()
        {
            Assert.Equal(Path.Combine(_base, "home", ".claude.json"), ClaudeProfileDataRoots.DefaultClaudeJsonPath(_ctx));

            var inherited = _ctx with { InheritedConfigDir = _workDir };
            Assert.Equal(Path.Combine(_workDir, ".claude.json"), ClaudeProfileDataRoots.DefaultClaudeJsonPath(inherited));
        }
    }
}
