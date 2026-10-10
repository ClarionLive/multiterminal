using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket c8ee0eec (GitHub #21), Phase 2 review fixes: <see cref="ClaudeProfileLaunchPolicy"/>, the one
    /// decision every launch site asks ("which Claude profile, and may it start"). The database rows,
    /// the project.json files and the configured profiles are all handed in, so nothing reads the live
    /// database, settings.txt or a real project. The worktree facts build a throwaway folder tree under
    /// the temp directory, because that part of the decision looks for a <c>.git</c> on disk.
    /// <para>NOT covered: that each launch site in MainForm calls the policy, passes the right folder, or
    /// honours a refusal; and how MainForm finds a spawner's pane. Those need a running form.</para>
    /// </summary>
    public sealed class ClaudeProfileLaunchPolicyTests : IDisposable
    {
        private const string Home = @"C:\Users\Dev";
        private const string WorkDir = Home + @"\.claude-work";
        private const string PersonalDir = Home + @"\.claude-personal";
        private const string Repo = @"D:\src\acme";

        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"mt_cprofilepolicy_{Guid.NewGuid():N}");

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static ClaudeProfileProjectRow Row(string id, string profile, string path = Repo, string sourcePath = null, string name = null) =>
            new ClaudeProfileProjectRow(id, name ?? ("Project " + id), path, sourcePath, profile);

        private static Project FileProject(string id, string profile) =>
            new Project { Id = id, Name = "File " + id, ClaudeProfile = profile };

        /// <summary>A lookup over fixed rows and files; folders are taken as given (no worktree mapping).</summary>
        private static ClaudeProfileLookup Lookup(
            IEnumerable<ClaudeProfileProjectRow> rows = null,
            IDictionary<string, Project> files = null,
            Func<string, IReadOnlyList<string>> candidates = null)
        {
            var rowList = (rows ?? Array.Empty<ClaudeProfileProjectRow>()).ToList();
            var fileMap = new Dictionary<string, Project>(files ?? new Dictionary<string, Project>(), StringComparer.OrdinalIgnoreCase);
            return new ClaudeProfileLookup(
                RowById: id => rowList.FirstOrDefault(r => r.Id == id),
                AllRows: () => rowList,
                LoadProjectFile: folder => folder != null && fileMap.TryGetValue(folder, out var p) ? p : null,
                CandidateFolders: candidates ?? (folder => folder == null ? Array.Empty<string>() : new[] { folder }));
        }

        private static Func<string, ClaudeProfileResolution> Resolver(Func<string, bool> exists = null) =>
            name => ClaudeProfileResolver.Resolve(
                name,
                null,
                new ClaudeProfileContext(
                    new[] { new ClaudeProfile("Work", WorkDir), new ClaudeProfile("Personal", PersonalDir) },
                    null,
                    Home,
                    exists ?? (_ => true)));

        // ── The database row is the authority ────────────────────────────────

        [Fact]
        public void A_named_project_takes_its_database_row_even_when_the_file_disagrees()
        {
            var lookup = Lookup(
                rows: new[] { Row("p1", "Work") },
                files: new Dictionary<string, Project> { [Repo] = FileProject("p1", "Personal") });

            var name = ClaudeProfileLaunchPolicy.FindProfileName("p1", Repo, null, lookup);

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, name.Source);
            Assert.Equal("p1", name.ProjectId);
        }

        [Fact]
        public void A_row_set_to_Default_beats_a_stale_file_that_still_names_a_profile()
        {
            // The exact stale-file case: the profile was cleared in MultiTerminal, the project.json copy
            // was not rewritten. A "use the first non-blank value" lookup would launch under Work.
            var lookup = Lookup(
                rows: new[] { Row("p1", null) },
                files: new Dictionary<string, Project> { [Repo] = FileProject("p1", "Work") });

            var byFolder = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, null, lookup);
            var byId = ClaudeProfileLaunchPolicy.FindProfileName("p1", Repo, null, lookup);

            Assert.Null(byFolder.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, byFolder.Source);
            Assert.Null(byId.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, byId.Source);
        }

        [Fact]
        public void A_folder_whose_file_names_a_project_takes_that_projects_row()
        {
            // The row lives at another path (a moved folder): the file says WHICH project, the row says
            // its profile.
            var lookup = Lookup(
                rows: new[] { Row("p1", "Work", path: @"D:\old\place") },
                files: new Dictionary<string, Project> { [Repo] = FileProject("p1", "Personal") });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, null, lookup);

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, name.Source);
        }

        [Theory]
        [InlineData(@"D:\src\acme")]
        [InlineData(@"d:\SRC\acme\")]
        public void A_folder_with_no_file_is_matched_to_a_row_by_path_or_source_path(string folder)
        {
            var byPath = Lookup(rows: new[] { Row("p1", "Work", path: Repo) });
            var bySource = Lookup(rows: new[] { Row("p1", "Work", path: @"D:\elsewhere", sourcePath: Repo + @"\") });

            Assert.Equal("Work", ClaudeProfileLaunchPolicy.FindProfileName(null, folder, null, byPath).ProfileName);
            Assert.Equal("Work", ClaudeProfileLaunchPolicy.FindProfileName(null, folder, null, bySource).ProfileName);
        }

        [Fact]
        public void The_file_is_used_only_when_the_project_has_no_row()
        {
            var files = new Dictionary<string, Project> { [Repo] = FileProject("p1", "Personal") };

            var noRow = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, null, Lookup(files: files));
            Assert.Equal("Personal", noRow.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.ProjectFile, noRow.Source);

            // Same file, but now the project has a row: the file's value is not consulted.
            var withRow = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, null, Lookup(new[] { Row("p1", "Work", path: @"D:\x") }, files));
            Assert.Equal("Work", withRow.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, withRow.Source);
        }

        [Fact]
        public void A_named_project_with_no_row_falls_to_the_folder()
        {
            // Nothing knows "ghost"; the folder belongs to p1, so p1's row decides.
            var lookup = Lookup(rows: new[] { Row("p1", "Work") });

            var name = ClaudeProfileLaunchPolicy.FindProfileName("ghost", Repo, "Personal", lookup);

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal("p1", name.ProjectId);
        }

        // ── No project: the inherited (spawner's) profile, else Default ──────

        [Fact]
        public void A_folder_no_project_owns_inherits_the_given_profile()
        {
            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, @"D:\scratch", " Work ", Lookup(rows: new[] { Row("p1", "Personal") }));

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Inherited, name.Source);
            Assert.Null(name.ProjectId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Default")]
        public void A_folder_no_project_owns_with_nothing_to_inherit_is_Default(string inherited)
        {
            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, @"D:\scratch", inherited, Lookup());

            Assert.Null(name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.None, name.Source);
        }

        [Fact]
        public void A_folder_that_belongs_to_a_project_ignores_the_inherited_profile()
        {
            // A helper spawned by a pane on Personal into a Work project runs under Work...
            var work = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, "Personal", Lookup(rows: new[] { Row("p1", "Work") }));
            Assert.Equal("Work", work.ProfileName);

            // ...and into a project set to Default runs under Default, not under the spawner's profile.
            var def = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, "Personal", Lookup(rows: new[] { Row("p1", null) }));
            Assert.Null(def.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, def.Source);
        }

        // ── Worktrees ────────────────────────────────────────────────────────

        private string MakeRepoWithWorktree(out string worktree)
        {
            string repo = Path.Combine(_tempRoot, "acme");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            worktree = Path.Combine(repo, ".claude", "worktrees", "c8ee0eec");
            Directory.CreateDirectory(Path.Combine(worktree, "Services"));
            Directory.CreateDirectory(Path.Combine(repo, "Services"));
            return repo;
        }

        [Fact]
        public void A_task_worktree_is_looked_up_at_itself_and_then_at_its_repo()
        {
            string repo = MakeRepoWithWorktree(out string worktree);

            Assert.Equal(new[] { worktree, repo }, ClaudeProfileLaunchPolicy.CandidateFolders(worktree));

            // A folder inside the worktree maps to the same folder in the main checkout, then the repo root.
            Assert.Equal(
                new[] { Path.Combine(worktree, "Services"), Path.Combine(repo, "Services"), repo },
                ClaudeProfileLaunchPolicy.CandidateFolders(Path.Combine(worktree, "Services")));
        }

        [Fact]
        public void A_pane_in_a_task_worktree_takes_the_profile_of_the_project_at_its_repo()
        {
            string repo = MakeRepoWithWorktree(out string worktree);
            var lookup = Lookup(rows: new[] { Row("p1", "Work", path: repo) }, candidates: ClaudeProfileLaunchPolicy.CandidateFolders);

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, worktree, "Personal", lookup);

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, name.Source);
        }

        [Fact]
        public void A_row_at_the_repo_beats_a_rowless_project_file_inside_the_worktree()
        {
            // A worktree that carries its own (checked-in) project.json whose id has no row.
            string repo = MakeRepoWithWorktree(out string worktree);
            var lookup = Lookup(
                rows: new[] { Row("p1", "Work", path: repo) },
                files: new Dictionary<string, Project> { [worktree] = FileProject("copy", "Personal") },
                candidates: ClaudeProfileLaunchPolicy.CandidateFolders);

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, worktree, null, lookup);

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, name.Source);
        }

        [Fact]
        public void An_ordinary_subfolder_of_a_project_is_not_matched_to_it()
        {
            // Pins a LIMIT, not a goal: only the project's own folder (and its worktrees) match, so a
            // pane opened in <repo>\Services belongs to no project and gets Default or the inherited
            // profile. Recorded here so nobody reads the worktree facts above as "any folder inside".
            string repo = MakeRepoWithWorktree(out _);
            string sub = Path.Combine(repo, "Services");
            var lookup = Lookup(rows: new[] { Row("p1", "Work", path: repo) }, candidates: ClaudeProfileLaunchPolicy.CandidateFolders);

            Assert.Equal(new[] { sub }, ClaudeProfileLaunchPolicy.CandidateFolders(sub));
            Assert.Equal(ClaudeProfileNameSource.None, ClaudeProfileLaunchPolicy.FindProfileName(null, sub, null, lookup).Source);
        }

        // ── Decide: launch or refuse ─────────────────────────────────────────

        [Fact]
        public void No_project_and_nothing_inherited_launches_under_Default()
        {
            var decision = ClaudeProfileLaunchPolicy.Decide(null, @"D:\scratch", null, "the terminal", Lookup(), Resolver());

            Assert.True(decision.CanLaunch);
            Assert.True(decision.Profile.IsDefault);
            Assert.Null(decision.Refusal);
        }

        [Fact]
        public void A_project_on_a_configured_profile_launches_under_it()
        {
            var decision = ClaudeProfileLaunchPolicy.Decide("p1", Repo, null, "project 'Acme'", Lookup(rows: new[] { Row("p1", "work") }), Resolver());

            Assert.True(decision.CanLaunch);
            Assert.Equal("Work", decision.Profile.Name);
            Assert.Equal(WorkDir, decision.Profile.ConfigDir);
        }

        [Fact]
        public void A_project_on_a_profile_this_machine_lacks_is_refused_and_the_refusal_says_what_to_fix()
        {
            var lookup = Lookup(rows: new[] { Row("p1", "ClientX", name: "Acme") });

            var decision = ClaudeProfileLaunchPolicy.Decide("p1", Repo, null, "project 'Acme'", lookup, Resolver());

            Assert.False(decision.CanLaunch);
            Assert.Null(decision.Profile);
            Assert.Contains("Not started: project 'Acme'", decision.Refusal, StringComparison.Ordinal);
            Assert.Contains("Project 'Acme' is set to the Claude profile 'ClientX'", decision.Refusal, StringComparison.Ordinal);
            Assert.Contains("not configured on this machine", decision.Refusal, StringComparison.Ordinal);
            Assert.Contains("Settings > Claude Profiles", decision.Refusal, StringComparison.Ordinal);
            Assert.Contains("Edit Project", decision.Refusal, StringComparison.Ordinal);
        }

        [Fact]
        public void A_project_whose_profile_folder_is_missing_is_refused()
        {
            var decision = ClaudeProfileLaunchPolicy.Decide(
                "p1", Repo, null, "project 'Acme'", Lookup(rows: new[] { Row("p1", "Work") }), Resolver(exists: _ => false));

            Assert.False(decision.CanLaunch);
            Assert.Null(decision.Profile);
            Assert.Contains("does not exist", decision.Refusal, StringComparison.Ordinal);
        }

        [Fact]
        public void An_inherited_profile_this_machine_lacks_is_refused_and_says_where_the_name_came_from()
        {
            var decision = ClaudeProfileLaunchPolicy.Decide(null, @"D:\scratch", "Gone", "helper 'Bob'", Lookup(), Resolver());

            Assert.False(decision.CanLaunch);
            Assert.Equal(ClaudeProfileNameSource.Inherited, decision.Source);
            Assert.Contains("belongs to no project", decision.Refusal, StringComparison.Ordinal);
            Assert.Contains("'Gone'", decision.Refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("Edit Project", decision.Refusal, StringComparison.Ordinal);
        }

        [Fact]
        public void A_lookup_that_fails_is_a_refusal_not_a_launch_under_Default()
        {
            var broken = new ClaudeProfileLookup(
                RowById: _ => throw new InvalidOperationException("database is locked"),
                AllRows: () => throw new InvalidOperationException("database is locked"),
                LoadProjectFile: _ => null,
                CandidateFolders: folder => new[] { folder });

            var named = ClaudeProfileLaunchPolicy.Decide("p1", Repo, null, "project 'Acme'", broken, Resolver());
            var byFolder = ClaudeProfileLaunchPolicy.Decide(null, Repo, null, "the terminal", broken, Resolver());

            Assert.False(named.CanLaunch);
            Assert.Null(named.Profile);
            Assert.Contains("database is locked", named.Refusal, StringComparison.Ordinal);
            Assert.False(byFolder.CanLaunch);
            Assert.Null(byFolder.Profile);
        }
    }
}
