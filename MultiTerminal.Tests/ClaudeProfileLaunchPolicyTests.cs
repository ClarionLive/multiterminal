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
    /// database, settings.txt or a real project. Every fact runs the production
    /// <see cref="ClaudeProfileLaunchPolicy.CandidateFolders"/> (the ancestor walk) unless it passes its
    /// own. The worktree facts build a throwaway folder tree under the temp directory, because that part
    /// of the decision looks for a <c>.git</c> on disk; the other paths are made up and never touched.
    /// <para>Ownership as pinned here: above the launch folder only a database row registered at that
    /// exact folder owns the launch, and a project.json there is never read. At the launch folder (and
    /// the same folder in the main checkout, for a worktree) a registered row decides; with none, that
    /// folder's project.json counts by the profile name written in it, and its project id is followed
    /// to a row only when that row is registered at the launch folder or its twin.</para>
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

        /// <summary>A lookup over fixed rows and files, with the production ancestor walk unless
        /// <paramref name="candidates"/> replaces it.</summary>
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
                CandidateFolders: candidates ?? ClaudeProfileLaunchPolicy.CandidateFolders);
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
        public void A_file_at_the_launch_folder_naming_a_project_registered_elsewhere_is_a_copy_with_no_row()
        {
            // The file's id belongs to a row registered at ANOTHER path. That row is a different
            // folder's project, so the id is not followed: the folder takes the profile written in its
            // own file. Following the id would give Work here, so the two rules are distinguishable.
            var lookup = Lookup(
                rows: new[] { Row("p1", "Work", path: @"D:\old\place") },
                files: new Dictionary<string, Project> { [Repo] = FileProject("p1", "Personal") });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, "Work", lookup);

            Assert.Equal("Personal", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.ProjectFile, name.Source);

            // A file that names no profile means Default there, and still does not reach the row.
            var blank = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, "Work", Lookup(
                rows: new[] { Row("p1", "Work", path: @"D:\old\place") },
                files: new Dictionary<string, Project> { [Repo] = FileProject("p1", null) }));

            Assert.Null(blank.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.ProjectFile, blank.Source);
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
        public void The_file_at_the_launch_folder_is_used_only_when_no_row_is_registered_there()
        {
            var files = new Dictionary<string, Project> { [Repo] = FileProject("p1", "Personal") };

            // No row anywhere: the launch folder's own file counts, ahead of the spawner's profile.
            var noRow = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, "Work", Lookup(files: files));
            Assert.Equal("Personal", noRow.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.ProjectFile, noRow.Source);

            // Same file, but a row (under any id) is registered at the folder: the file's value is not used.
            var withRow = ClaudeProfileLaunchPolicy.FindProfileName(null, Repo, null, Lookup(new[] { Row("other", "Work", path: Repo) }, files));
            Assert.Equal("Work", withRow.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, withRow.Source);
            Assert.Equal("other", withRow.ProjectId);
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
        public void A_task_worktree_is_looked_up_at_itself_then_in_the_main_checkout_then_up_the_tree()
        {
            string repo = MakeRepoWithWorktree(out string worktree);

            var atWorktree = ClaudeProfileLaunchPolicy.CandidateFolders(worktree);
            Assert.Equal(new[] { worktree, repo }, atWorktree.Take(2));

            // A folder inside the worktree: each worktree-side level is followed at once by the same
            // folder in the main checkout, and only then come the folders between the worktree and the
            // repo and everything above the repo.
            var inside = ClaudeProfileLaunchPolicy.CandidateFolders(Path.Combine(worktree, "Services"));
            Assert.Equal(
                new[]
                {
                    Path.Combine(worktree, "Services"),
                    Path.Combine(repo, "Services"),
                    worktree,
                    repo,
                    Path.Combine(repo, ".claude", "worktrees"),
                    Path.Combine(repo, ".claude"),
                    _tempRoot,
                },
                inside.Take(7));
            Assert.Equal(inside.Count, inside.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
        public void A_worktree_with_any_name_belongs_to_the_project_at_its_repo()
        {
            // "c8ee0eec-i5" is not an 8-hex task id, so WorktreeLayout maps it to nothing. The repo is
            // an ancestor, and that is what finds the project.
            string repo = MakeRepoWithWorktree(out _);
            string oddWorktree = Path.Combine(repo, ".claude", "worktrees", "c8ee0eec-i5");
            Assert.False(WorktreeLayout.TryMapWorktreePath(oddWorktree, out _, out _));
            var lookup = Lookup(rows: new[] { Row("p1", "Work", path: repo) });

            var atWorktree = ClaudeProfileLaunchPolicy.FindProfileName(null, oddWorktree, "Personal", lookup);
            var below = ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(oddWorktree, "Services"), "Personal", lookup);

            Assert.Equal("Work", atWorktree.ProfileName);
            Assert.Equal("p1", atWorktree.ProjectId);
            Assert.Equal("Work", below.ProfileName);
            Assert.Equal("p1", below.ProjectId);
        }

        [Fact]
        public void A_folder_in_a_worktree_belongs_to_the_project_registered_at_that_folder_in_the_main_checkout()
        {
            // A project registered at <repo>\Services owns <worktree>\Services ahead of the repo's own
            // project, for a task-id worktree and for one with any other name.
            string repo = MakeRepoWithWorktree(out string worktree);
            string oddWorktree = Path.Combine(repo, ".claude", "worktrees", "c8ee0eec-i5");
            var lookup = Lookup(rows: new[]
            {
                Row("root", "Work", path: repo),
                Row("sub", "Personal", path: Path.Combine(repo, "Services")),
            });

            Assert.Equal("sub", ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(worktree, "Services"), null, lookup).ProjectId);
            Assert.Equal("sub", ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(oddWorktree, "Services"), null, lookup).ProjectId);
            Assert.Equal("root", ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(oddWorktree, "Docs"), null, lookup).ProjectId);
        }

        [Fact]
        public void A_worktrees_copy_of_the_repo_project_file_does_not_take_a_nested_folder_from_its_own_project()
        {
            // The worktree carries a copy of the repo's project.json, naming the root project. A launch
            // in <worktree>\Services belongs to the project registered at <repo>\Services.
            string repo = MakeRepoWithWorktree(out string worktree);
            var lookup = Lookup(
                rows: new[]
                {
                    Row("root", "Work", path: repo),
                    Row("sub", "Personal", path: Path.Combine(repo, "Services")),
                },
                files: new Dictionary<string, Project> { [worktree] = FileProject("root", "Work") });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(worktree, "Services"), "Work", lookup);

            Assert.Equal("Personal", name.ProfileName);
            Assert.Equal("sub", name.ProjectId);
            Assert.Equal(ClaudeProfileNameSource.Database, name.Source);
        }

        [Fact]
        public void A_project_file_at_the_same_folder_in_the_main_checkout_counts_for_a_launch_in_the_worktree()
        {
            // No row anywhere. The launch folder's twin in the main checkout is a launch level too, so
            // its project.json counts; the repo root's (above the launch folder) does not.
            string repo = MakeRepoWithWorktree(out string worktree);
            var lookup = Lookup(files: new Dictionary<string, Project>
            {
                [Path.Combine(repo, "Services")] = FileProject("sub", "Personal"),
                [repo] = FileProject("root", "Work"),
                [worktree] = FileProject("root", "Work"),
            });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(worktree, "Services"), null, lookup);

            Assert.Equal("Personal", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.ProjectFile, name.Source);
        }

        [Fact]
        public void A_worktrees_project_file_is_followed_to_the_row_registered_at_its_main_checkout()
        {
            // The one case where a file's id IS followed past the launch folder: the row is registered
            // at the launch folder's twin. A second project registered at the worktree itself under
            // another profile then makes two claims on the launch, which refuses. Without following the
            // id, the worktree's own row would win alone.
            string repo = MakeRepoWithWorktree(out string worktree);
            var lookup = Lookup(
                rows: new[] { Row("root", "Work", path: repo), Row("wt", "Personal", path: worktree) },
                files: new Dictionary<string, Project> { [worktree] = FileProject("root", null) });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, worktree, null, lookup);

            Assert.NotNull(name.Conflict);
            Assert.Null(name.ProfileName);
        }

        [Fact]
        public void A_legacy_sibling_worktree_is_found_through_the_layout_mapping_not_the_ancestor_walk()
        {
            // <parent>\acme-worktrees\<8-hex id>: the repo is NOT an ancestor of the worktree.
            string repo = MakeRepoWithWorktree(out _);
            string sibling = Path.Combine(_tempRoot, "acme-worktrees", "0a1b2c3d");
            Directory.CreateDirectory(sibling);
            var lookup = Lookup(rows: new[] { Row("p1", "Work", path: repo) });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(sibling, "Services"), "Personal", lookup);

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal("p1", name.ProjectId);
        }

        // ── Subfolders: the nearest registered project at or above the folder ─

        private const string WorkProject = @"H:\Work\A";
        private const string PersonalProject = @"H:\Personal\B";

        [Fact]
        public void A_subfolder_belongs_to_the_project_above_it()
        {
            // The opposite of the limit this file used to pin. A helper spawned from a Work pane into
            // H:\Personal\B\src runs under Personal; Just Claude in H:\Work\A\src runs under Work.
            var lookup = Lookup(rows: new[] { Row("work", "Work", path: WorkProject), Row("personal", "Personal", path: PersonalProject) });

            var helper = ClaudeProfileLaunchPolicy.FindProfileName(null, PersonalProject + @"\src", "Work", lookup);
            var deep = ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\src\a\b\c", null, lookup);

            Assert.Equal("Personal", helper.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, helper.Source);
            Assert.Equal("personal", helper.ProjectId);
            Assert.Equal("Work", deep.ProfileName);
            Assert.Equal("work", deep.ProjectId);
        }

        [Fact]
        public void A_subfolder_is_matched_by_source_path()
        {
            var bySource = Lookup(rows: new[] { Row("p1", "Work", path: @"D:\elsewhere", sourcePath: WorkProject) });

            Assert.Equal("Work", ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\src", null, bySource).ProfileName);
        }

        // ── A project.json above the launch folder owns nothing ──────────────

        private const string BroadParent = @"H:\Work";
        private const string UnregisteredChild = @"H:\Work\repo";

        [Fact]
        public void A_project_file_in_a_parent_folder_does_not_own_an_unregistered_child_folder()
        {
            // A stale or copied H:\Work\.claude\project.json naming Personal. Nothing is registered at
            // or above H:\Work\repo, so the launch there belongs to no project.
            var lookup = Lookup(files: new Dictionary<string, Project> { [BroadParent] = FileProject("stale", "Personal") });

            var fromSpawner = ClaudeProfileLaunchPolicy.FindProfileName(null, UnregisteredChild, "Work", lookup);
            var alone = ClaudeProfileLaunchPolicy.FindProfileName(null, UnregisteredChild, null, lookup);

            Assert.Equal("Work", fromSpawner.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Inherited, fromSpawner.Source);
            Assert.Null(fromSpawner.ProjectId);
            Assert.Null(alone.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.None, alone.Source);

            // The same file DOES count for a launch in H:\Work itself, so the two facts above are not
            // passing because the file is unreadable.
            var atParent = ClaudeProfileLaunchPolicy.FindProfileName(null, BroadParent, "Work", lookup);
            Assert.Equal("Personal", atParent.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.ProjectFile, atParent.Source);
        }

        [Fact]
        public void A_parent_project_file_naming_a_project_registered_elsewhere_does_not_own_the_child()
        {
            // The parent's file carries the id of a real project whose row is registered at another
            // path. Neither the file nor that row owns H:\Work\repo.
            var lookup = Lookup(
                rows: new[] { Row("personal", "Personal", path: PersonalProject) },
                files: new Dictionary<string, Project> { [BroadParent] = FileProject("personal", "Personal") });

            var fromSpawner = ClaudeProfileLaunchPolicy.FindProfileName(null, UnregisteredChild, "Work", lookup);
            var alone = ClaudeProfileLaunchPolicy.FindProfileName(null, UnregisteredChild, null, lookup);

            Assert.Equal("Work", fromSpawner.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Inherited, fromSpawner.Source);
            Assert.Null(alone.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.None, alone.Source);
        }

        [Fact]
        public void A_row_registered_at_the_parent_owns_the_child_whatever_the_parents_file_says()
        {
            var lookup = Lookup(
                rows: new[] { Row("work", "Work", path: BroadParent) },
                files: new Dictionary<string, Project> { [BroadParent] = FileProject("stale", "Personal") });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, UnregisteredChild, "Personal", lookup);

            Assert.Equal("Work", name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, name.Source);
            Assert.Equal("work", name.ProjectId);
        }

        [Fact]
        public void Project_files_are_asked_for_only_at_the_launch_folder_and_its_main_checkout_twin()
        {
            // Not "read and ignored": a folder above the launch folder is never asked for its file.
            var asked = new List<string>();
            ClaudeProfileLookup Recording() => new ClaudeProfileLookup(
                RowById: _ => null,
                AllRows: () => Array.Empty<ClaudeProfileProjectRow>(),
                LoadProjectFile: folder => { asked.Add(folder); return null; },
                CandidateFolders: ClaudeProfileLaunchPolicy.CandidateFolders);

            ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\src\deep", null, Recording());
            Assert.Equal(new[] { WorkProject + @"\src\deep" }, asked);

            string repo = MakeRepoWithWorktree(out string worktree);
            asked.Clear();
            ClaudeProfileLaunchPolicy.FindProfileName(null, Path.Combine(worktree, "Services"), null, Recording());
            Assert.Equal(new[] { Path.Combine(worktree, "Services"), Path.Combine(repo, "Services") }, asked);
        }

        [Fact]
        public void A_nested_project_beats_the_project_around_it()
        {
            var lookup = Lookup(rows: new[]
            {
                Row("outer", "Work", path: WorkProject),
                Row("inner", "Personal", path: WorkProject + @"\vendor\tool"),
            });

            Assert.Equal("inner", ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\vendor\tool\src", null, lookup).ProjectId);
            Assert.Equal("inner", ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\vendor\tool", null, lookup).ProjectId);
            Assert.Equal("outer", ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\vendor", null, lookup).ProjectId);
        }

        [Fact]
        public void A_project_file_with_no_row_beats_a_farther_row_only_for_a_launch_in_its_own_folder()
        {
            var lookup = Lookup(
                rows: new[] { Row("outer", "Work", path: WorkProject) },
                files: new Dictionary<string, Project> { [WorkProject + @"\vendor\tool"] = FileProject("inner", "Personal") });

            // Launched in the folder that holds the file: no row is registered there, so its file is
            // the project, and the outer project's row does not reach past it.
            var atFile = ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\vendor\tool", null, lookup);
            Assert.Equal("Personal", atFile.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.ProjectFile, atFile.Source);

            // Launched one folder below: the file is above the launch folder and owns nothing, so the
            // nearest registered row decides.
            var below = ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\vendor\tool\src", null, lookup);
            Assert.Equal("Work", below.ProfileName);
            Assert.Equal("outer", below.ProjectId);
        }

        [Fact]
        public void A_project_does_not_own_a_folder_whose_name_merely_starts_with_its_own()
        {
            // H:\Proj must not own H:\Proj2\x. The positive half is here too, so this fact cannot pass
            // just because nothing is ever matched.
            var lookup = Lookup(rows: new[] { Row("p1", "Work", path: @"H:\Proj") });

            var sibling = ClaudeProfileLaunchPolicy.FindProfileName(null, @"H:\Proj2\x", null, lookup);
            var siblingItself = ClaudeProfileLaunchPolicy.FindProfileName(null, @"H:\Proj2", null, lookup);
            var child = ClaudeProfileLaunchPolicy.FindProfileName(null, @"H:\Proj\x", null, lookup);

            Assert.Equal(ClaudeProfileNameSource.None, sibling.Source);
            Assert.Equal(ClaudeProfileNameSource.None, siblingItself.Source);
            Assert.Equal("p1", child.ProjectId);
        }

        [Theory]
        [InlineData(@"H:\Work\A\src\")]
        [InlineData(@"H:/Work/A/src")]
        [InlineData(@"h:\WORK\a\SRC")]
        [InlineData(@"H:\Work\A\src\..\src\deep")]
        public void Ownership_ignores_case_trailing_separators_and_forward_slashes(string folder)
        {
            var lookup = Lookup(rows: new[] { Row("p1", "Work", path: @"H:\Work\A\") });

            Assert.Equal("p1", ClaudeProfileLaunchPolicy.FindProfileName(null, folder, null, lookup).ProjectId);
        }

        [Fact]
        public void An_owning_project_with_no_profile_means_Default_and_not_the_spawners_profile()
        {
            // A personal project that names no profile, entered from a pane running under Work.
            var lookup = Lookup(rows: new[] { Row("personal", null, path: PersonalProject) });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, PersonalProject + @"\src", "Work", lookup);

            Assert.Null(name.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Database, name.Source);
            Assert.Equal("personal", name.ProjectId);
        }

        [Fact]
        public void A_nearer_project_with_no_profile_stops_the_search_before_a_farther_project()
        {
            var lookup = Lookup(rows: new[]
            {
                Row("outer", "Work", path: WorkProject),
                Row("inner", null, path: WorkProject + @"\vendor\tool"),
            });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject + @"\vendor\tool\src", "Personal", lookup);

            Assert.Null(name.ProfileName);
            Assert.Equal("inner", name.ProjectId);
        }

        [Fact]
        public void With_no_owner_at_any_level_the_spawners_profile_applies_else_Default()
        {
            var lookup = Lookup(rows: new[] { Row("work", "Work", path: WorkProject) });

            var inherited = ClaudeProfileLaunchPolicy.FindProfileName(null, @"H:\Scratch\a\b", "Personal", lookup);
            var nothing = ClaudeProfileLaunchPolicy.FindProfileName(null, @"H:\Scratch\a\b", null, lookup);

            Assert.Equal("Personal", inherited.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.Inherited, inherited.Source);
            Assert.Null(nothing.ProfileName);
            Assert.Equal(ClaudeProfileNameSource.None, nothing.Source);
        }

        [Fact]
        public void The_volume_root_is_searched_only_when_it_is_the_folder_itself()
        {
            // A decision of this implementation, not of the Owner: a project registered at a drive root
            // does not own every folder on the drive.
            var lookup = Lookup(rows: new[] { Row("drive", "Work", path: @"H:\") });

            Assert.Equal(new[] { @"H:\a\b", @"H:\a" }, ClaudeProfileLaunchPolicy.CandidateFolders(@"H:\a\b"));
            Assert.Equal(ClaudeProfileNameSource.None, ClaudeProfileLaunchPolicy.FindProfileName(null, @"H:\a\b", null, lookup).Source);
            Assert.Equal("drive", ClaudeProfileLaunchPolicy.FindProfileName(null, @"H:\", null, lookup).ProjectId);
        }

        // ── Two projects at one folder ───────────────────────────────────────

        [Fact]
        public void Two_projects_at_the_owning_folder_with_different_profiles_refuse_the_launch()
        {
            var lookup = Lookup(rows: new[]
            {
                Row("p1", "Work", path: WorkProject, name: "Acme"),
                Row("p2", null, path: @"D:\elsewhere", sourcePath: @"h:\work\a\", name: "Acme copy"),
            });

            var decision = ClaudeProfileLaunchPolicy.Decide(null, WorkProject + @"\src", "Work", "Claude in 'src'", lookup, Resolver());

            Assert.False(decision.CanLaunch);
            Assert.Null(decision.Profile);
            Assert.Contains("Not started: Claude in 'src'", decision.Refusal, StringComparison.Ordinal);
            Assert.Contains(WorkProject, decision.Refusal, StringComparison.Ordinal);
            Assert.Contains("'Acme' (Work)", decision.Refusal, StringComparison.Ordinal);
            Assert.Contains("'Acme copy' (Default)", decision.Refusal, StringComparison.Ordinal);
        }

        [Fact]
        public void Two_projects_at_the_owning_folder_with_the_same_profile_launch_under_it()
        {
            var lookup = Lookup(rows: new[]
            {
                Row("p1", "Work", path: WorkProject),
                Row("p2", " work ", path: WorkProject + @"\"),
            });

            var decision = ClaudeProfileLaunchPolicy.Decide(null, WorkProject + @"\src", null, "Claude in 'src'", lookup, Resolver());

            Assert.True(decision.CanLaunch);
            Assert.Equal("Work", decision.Profile.Name);
        }

        [Fact]
        public void A_conflict_farther_up_does_not_matter_when_a_nearer_project_owns_the_folder()
        {
            var lookup = Lookup(rows: new[]
            {
                Row("p1", "Work", path: WorkProject),
                Row("p2", "Personal", path: WorkProject),
                Row("inner", "Personal", path: WorkProject + @"\vendor\tool"),
            });

            var inner = ClaudeProfileLaunchPolicy.Decide(null, WorkProject + @"\vendor\tool\src", null, "x", lookup, Resolver());
            var outer = ClaudeProfileLaunchPolicy.Decide(null, WorkProject + @"\vendor", null, "x", lookup, Resolver());

            Assert.True(inner.CanLaunch);
            Assert.Equal("Personal", inner.Profile.Name);
            Assert.False(outer.CanLaunch);
        }

        [Fact]
        public void A_file_naming_a_project_registered_elsewhere_does_not_contest_the_row_registered_at_the_folder()
        {
            // The file says the folder is p1, whose row is registered at another path; p2 is registered
            // at the folder itself. Only p2 claims it: no conflict, and p1's profile plays no part.
            var lookup = Lookup(
                rows: new[] { Row("p1", "Work", path: @"D:\old\place"), Row("p2", "Personal", path: WorkProject) },
                files: new Dictionary<string, Project> { [WorkProject] = FileProject("p1", null) });

            var name = ClaudeProfileLaunchPolicy.FindProfileName(null, WorkProject, null, lookup);

            Assert.Null(name.Conflict);
            Assert.Equal("Personal", name.ProfileName);
            Assert.Equal("p2", name.ProjectId);
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
