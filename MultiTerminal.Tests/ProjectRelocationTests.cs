using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>Move Project (ticket 6f879683): path arithmetic, the checks, the database rewrite and the move.</summary>
    public sealed class ProjectRelocationTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly string _sandbox;
        private readonly TaskDatabase _taskDb;
        private readonly ProjectDatabase _projectDb;

        public ProjectRelocationTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_relocate_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _taskDb = new TaskDatabase();       // creates task, notes, session and worktree tables
            _projectDb = new ProjectDatabase(); // creates projects (incl. previous_paths) and project_paths
            _sandbox = Path.Combine(Path.GetTempPath(), $"mt_relocate_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
        }

        public void Dispose()
        {
            _projectDb?.Dispose();
            _taskDb?.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var f in new[] { _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm" })
            {
                if (File.Exists(f)) File.Delete(f);
            }

            if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        // ── RelocationPaths ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData(@"C:\foo", @"C:\foo", true)]
        [InlineData(@"C:\foo\bar", @"C:\foo", true)]
        [InlineData(@"c:/FOO/bar", @"C:\foo", true)]
        [InlineData(@"C:\foobar", @"C:\foo", false)]
        [InlineData(@"C:\foobar\x", @"C:\foo\", false)]
        [InlineData(@"C:\fo", @"C:\foo", false)]
        [InlineData(null, @"C:\foo", false)]
        public void IsSameOrUnder_is_separator_bounded(string candidate, string root, bool expected) =>
            Assert.Equal(expected, RelocationPaths.IsSameOrUnder(candidate, root));

        [Theory]
        [InlineData(@"C:\old\proj", @"D:\new\proj")]
        [InlineData(@"C:\old\proj\src\a.cs", @"D:\new\proj\src\a.cs")]
        [InlineData(@"C:/old/proj/src/a.cs", @"D:/new/proj/src/a.cs")]
        [InlineData(@"C:\old\projX\a.cs", null)]
        [InlineData(@"E:\elsewhere", null)]
        public void Rebase_moves_only_paths_under_the_old_root_and_keeps_their_separators(string path, string expected) =>
            Assert.Equal(expected, RelocationPaths.Rebase(path, @"C:\old\proj", @"D:\new\proj"));

        // ── Checks ──────────────────────────────────────────────────────────────────

        private static IReadOnlyList<string> Check(string newRoot, IEnumerable<RelocationPane> panes = null, string source = null,
            Func<string, bool> exists = null, Func<string, bool> entries = null) =>
            ProjectRelocationChecks.Check("p1", @"C:\work\proj", source, newRoot, moveFolder: true, panes,
                exists ?? (d => string.Equals(d, @"C:\work\proj", StringComparison.OrdinalIgnoreCase)), entries ?? (_ => false));

        [Fact]
        public void A_plain_move_has_no_problems() => Assert.Empty(Check(@"D:\new\proj"));

        [Theory]
        [InlineData(@"C:\work\proj")]
        [InlineData(@"C:\work\proj\sub")]
        [InlineData(@"C:\work")]
        [InlineData(@"D:\x\worktrees\abcd1234")]
        public void Refuses_same_inside_containing_and_worktree_looking_destinations(string to) =>
            Assert.NotEmpty(Check(to));

        [Fact]
        public void Refuses_a_non_empty_destination()
        {
            var problems = Check(@"D:\full", exists: _ => true, entries: d => d == @"D:\full");
            Assert.Contains(problems, p => p.Contains("not empty"));
        }

        [Fact]
        public void Refuses_while_a_pane_was_launched_for_the_project_or_sits_in_its_folder()
        {
            var launched = Check(@"D:\new", new[] { new RelocationPane("Alice", "p1", @"E:\elsewhere") });
            var inside = Check(@"D:\new", new[] { new RelocationPane("Shell", null, @"C:\work\proj\.claude\worktrees\abcd1234") });
            var unrelated = Check(@"D:\new", new[] { new RelocationPane("Other", "p2", @"C:\work\projX") });
            Assert.Contains(launched, p => p.Contains("Alice"));
            Assert.Contains(inside, p => p.Contains("Shell"));
            Assert.Empty(unrelated);
        }

        [Fact]
        public void Refuses_a_source_folder_outside_the_project_and_legacy_sibling_worktrees()
        {
            Assert.NotEmpty(Check(@"D:\new", source: @"C:\elsewhere\src"));
            Assert.Empty(Check(@"D:\new", source: @"C:\work\proj\src"));
            var sibling = Check(@"D:\new", exists: d => d == @"C:\work\proj" || d == @"C:\work\proj-worktrees", entries: d => d == @"C:\work\proj-worktrees");
            Assert.Contains(sibling, p => p.Contains("old layout"));
        }

        // ── Database rewrite ────────────────────────────────────────────────────────

        private SQLiteConnection Open()
        {
            var c = new SQLiteConnection($"Data Source={_testDbPath};Version=3;");
            c.Open();
            return c;
        }

        private static void Exec(SQLiteConnection c, string sql, params (string Name, object Value)[] args)
        {
#pragma warning disable CA2100 // test SQL, constant text
            using var cmd = new SQLiteCommand(sql, c);
#pragma warning restore CA2100
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static string Scalar(SQLiteConnection c, string sql)
        {
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sql, c);
#pragma warning restore CA2100
            return cmd.ExecuteScalar() as string;
        }

        private void SeedProject(SQLiteConnection c, string root, string deploy = null)
        {
            Exec(c, "INSERT INTO projects (id, name, path, deploy_path, created_at, updated_at) VALUES ('p1', 'Proj', @p, @d, datetime('now'), datetime('now'))", ("@p", root), ("@d", deploy));
            Exec(c, "INSERT INTO project_note_tabs (project_path, tab_name, content) VALUES (@p, 'Main', 'hello')", ("@p", root));
            Exec(c, "INSERT INTO task_file_links (id, task_id, file_path) VALUES (lower(hex(randomblob(8))), 't1', @f)", ("@f", root + @"\src\a.cs"));
            Exec(c, "INSERT INTO task_file_links (id, task_id, file_path) VALUES (lower(hex(randomblob(8))), 't1', 'relative\\b.cs')");
            Exec(c, "INSERT INTO task_file_links (id, task_id, file_path) VALUES (lower(hex(randomblob(8))), 't1', @f)", ("@f", root + @"X\c.cs"));
        }

        [Fact]
        public void Apply_repoints_everything_under_the_root_and_nothing_else()
        {
            using var c = Open();
            SeedProject(c, @"C:\old\proj", deploy: @"E:\deploy");

            var result = ProjectRelocationStore.Apply(c, "p1", @"C:\old\proj", @"D:\new\proj",
                new[] { (@"C:\cfg\projects\C--old-proj", @"C:\cfg\projects\D--new-proj") });

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(@"D:\new\proj", Scalar(c, "SELECT path FROM projects WHERE id='p1'"));
            Assert.Equal(@"E:\deploy", Scalar(c, "SELECT deploy_path FROM projects WHERE id='p1'"));
            Assert.Equal(@"D:\new\proj", Scalar(c, "SELECT project_path FROM project_note_tabs WHERE tab_name='Main'"));
            Assert.Equal(@"D:\new\proj\src\a.cs", Scalar(c, @"SELECT file_path FROM task_file_links WHERE file_path LIKE '%a.cs'"));
            Assert.Equal(@"relative\b.cs", Scalar(c, @"SELECT file_path FROM task_file_links WHERE file_path LIKE '%b.cs'"));
            Assert.Equal(@"C:\old\projX\c.cs", Scalar(c, @"SELECT file_path FROM task_file_links WHERE file_path LIKE '%c.cs'"));
            Assert.Equal(new[] { @"C:\old\proj" }, ProjectRelocationStore.GetPreviousPaths(c, "p1"));
        }

        [Fact]
        public void Apply_rolls_back_everything_when_notes_already_exist_for_the_new_folder()
        {
            using var c = Open();
            SeedProject(c, @"C:\old\proj");
            Exec(c, "INSERT INTO project_note_tabs (project_path, tab_name, content) VALUES ('D:\\new\\proj', 'Main', 'other')");

            Assert.NotEmpty(ProjectRelocationStore.FindConflicts(c, @"D:\new\proj"));
            var result = ProjectRelocationStore.Apply(c, "p1", @"C:\old\proj", @"D:\new\proj", null);

            Assert.False(result.Succeeded);
            Assert.Equal(@"C:\old\proj", Scalar(c, "SELECT path FROM projects WHERE id='p1'"));
            Assert.Equal(@"C:\old\proj\src\a.cs", Scalar(c, @"SELECT file_path FROM task_file_links WHERE file_path LIKE '%a.cs'"));
            Assert.Empty(ProjectRelocationStore.GetPreviousPaths(c, "p1"));
        }

        // ── Claude history and resume ───────────────────────────────────────────────

        [Fact]
        public void FindHistoryFolders_matches_both_encodings_and_worktree_folders_only()
        {
            string projects = Path.Combine(_sandbox, "projects");
            foreach (var name in new[] { "C--my-proj", "C--my-proj--claude-worktrees-abcd1234", "C--my-projX", "C--my proj" })
                Directory.CreateDirectory(Path.Combine(projects, name));

            var found = ProjectRelocationService.FindHistoryFolders(projects, @"C:\my proj")
                .Select(f => (Path.GetFileName(f.Folder), f.Suffix)).OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();

            Assert.Equal(new[]
            {
                ("C--my proj", ""),
                ("C--my-proj", ""),
                ("C--my-proj--claude-worktrees-abcd1234", "--claude-worktrees-abcd1234"),
            }, found);
        }

        [Fact]
        public void TranslateMovedCwds_reads_an_old_folder_as_the_new_one()
        {
            var cwds = new List<string> { @"C:\old\proj", @"C:\old\proj\.claude\worktrees\abcd1234", @"C:\old\projX", @"\\host\share" };
            SessionCatalog.TranslateMovedCwds(cwds, @"D:\new\proj", new[] { @"C:\old\proj" });
            Assert.Equal(new[] { @"D:\new\proj", @"D:\new\proj\.claude\worktrees\abcd1234", @"C:\old\projX", @"\\host\share" }, cwds);
        }

        // ── The move itself ─────────────────────────────────────────────────────────

        private ProjectRelocationService Service(string claudeRoot) => new ProjectRelocationService(new RelocationHost
        {
            ClaudeConfigRoots = new[] { claudeRoot },
            OpenDatabase = Open,
            RunGit = null,
        });

        [Fact]
        public void Move_on_one_drive_renames_the_folder_history_and_records()
        {
            string from = Path.Combine(_sandbox, "old", "proj");
            string to = Path.Combine(_sandbox, "new", "proj");
            Directory.CreateDirectory(Path.Combine(from, "src"));
            File.WriteAllText(Path.Combine(from, "src", "a.cs"), "class A {}");
            string claude = Path.Combine(_sandbox, "claude");
            string oldHistory = Path.Combine(claude, "projects", SessionCatalog.EncodeProjectPath(from));
            Directory.CreateDirectory(oldHistory);
            File.WriteAllText(Path.Combine(oldHistory, "s1.jsonl"), "{}");
            using (var c = Open()) SeedProject(c, from);

            var result = Service(claude).Move(new Project { Id = "p1", Name = "Proj", Path = from }, to);

            Assert.True(result.Succeeded, string.Join(" | ", result.Refusals));
            Assert.False(Directory.Exists(from));
            Assert.True(File.Exists(Path.Combine(to, "src", "a.cs")));
            Assert.True(File.Exists(Path.Combine(claude, "projects", SessionCatalog.EncodeProjectPath(to), "s1.jsonl")));
            Assert.Null(result.OldFolderKeptAt); // same drive: nothing left behind
            using var check = Open();
            Assert.Equal(to, Scalar(check, "SELECT path FROM projects WHERE id='p1'"));
        }

        [Fact]
        public void Move_puts_the_folder_and_history_back_when_the_records_cannot_be_updated()
        {
            string from = Path.Combine(_sandbox, "old", "proj");
            string to = Path.Combine(_sandbox, "new", "proj");
            Directory.CreateDirectory(from);
            string claude = Path.Combine(_sandbox, "claude");
            string oldHistory = Path.Combine(claude, "projects", SessionCatalog.EncodeProjectPath(from));
            Directory.CreateDirectory(oldHistory);
            using (var c = Open())
            {
                SeedProject(c, from);
                // A conflict the check does not see: a file link that collides once rebased.
                Exec(c, "INSERT INTO task_file_links (id, task_id, file_path) VALUES (lower(hex(randomblob(8))), 't1', @f)", ("@f", to + @"\src\a.cs"));
            }

            var result = Service(claude).Move(new Project { Id = "p1", Name = "Proj", Path = from }, to);

            Assert.False(result.Succeeded);
            Assert.True(Directory.Exists(from));
            Assert.False(Directory.Exists(to));
            Assert.True(Directory.Exists(oldHistory));
            using var check = Open();
            Assert.Equal(from, Scalar(check, "SELECT path FROM projects WHERE id='p1'"));
        }

        [Fact]
        public void Repoint_refuses_a_folder_holding_another_project()
        {
            string from = Path.Combine(_sandbox, "gone");
            string to = Path.Combine(_sandbox, "other");
            Directory.CreateDirectory(Path.Combine(to, ".claude"));
            File.WriteAllText(Path.Combine(to, ".claude", "project.json"), "{\"id\":\"someone-else\"}");
            using (var c = Open()) SeedProject(c, from);

            var result = Service(Path.Combine(_sandbox, "claude")).Repoint(new Project { Id = "p1", Name = "Proj", Path = from }, to);

            Assert.False(result.Succeeded);
            Assert.Contains(result.Refusals, r => r.Contains("different MT project"));
        }
    }
}
