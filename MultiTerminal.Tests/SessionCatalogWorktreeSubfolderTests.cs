using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 056bc6b6: a session filed under its worktree's folder whose recorded cwds are all BELOW the
    /// worktree root (a 46 MB transcript whose 64 KB tail held only <c>...\aea1b65e\tests\ClarionClone.Tests</c>)
    /// was dropped as "not this project's folder". The catalog now derives the worktree root from such a
    /// validated cwd and uses it only when that root encodes to the transcript's folder. Real temp folders.
    /// </summary>
    public sealed class SessionCatalogWorktreeSubfolderTests : IDisposable
    {
        private const string FolderReason = "it ran in a folder that is not this project's folder or one of its worktrees";
        private static readonly DateTime Now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _base;
        private readonly string _projectsRoot;

        public SessionCatalogWorktreeSubfolderTests()
        {
            _base = Path.Combine(Path.GetTempPath(), $"mt_scws_test_{Guid.NewGuid():N}");
            _projectsRoot = Path.Combine(_base, "projects");
            Directory.CreateDirectory(_projectsRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        }

        private static string Line(string type, string text, string cwd) => JsonSerializer.Serialize(new
        {
            type,
            message = new { role = type, content = text },
            uuid = Guid.NewGuid().ToString(),
            cwd,
        });

        // A transcript filed under the encoded folder of folderDir, whose turns ran in the given cwds.
        private string WriteSession(string folderDir, params string[] cwds)
        {
            var folder = Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(folderDir));
            Directory.CreateDirectory(folder);
            var id = Guid.NewGuid().ToString();
            var path = Path.Combine(folder, id + ".jsonl");
            var lines = cwds.Select((c, i) => Line(i == 0 ? "user" : "assistant", i == 0 ? "start" : "turn " + i, c));
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            File.SetLastWriteTimeUtc(path, Now.AddHours(-1));
            return id;
        }

        private SessionCatalog Catalog() => new SessionCatalog(_projectsRoot, null, null);

        private string NewProject(string name)
        {
            var root = Path.Combine(_base, name);
            Directory.CreateDirectory(root);
            return root;
        }

        private static string Worktree(string baseDir, string id)
        {
            var wt = Path.Combine(baseDir, ".claude", "worktrees", id);
            Directory.CreateDirectory(Path.Combine(wt, "tests", "ClarionClone.Tests"));
            return wt;
        }

        [Fact]
        public void Only_worktree_subfolder_cwds_resolve_to_the_worktree_root()
        {
            var root = NewProject("ClarionClone");
            var wt = Worktree(root, "aea1b65e");
            var sub = Path.Combine(wt, "tests", "ClarionClone.Tests");
            var id = WriteSession(wt, sub, sub, sub);

            var entry = Assert.Single(Catalog().List(root, new HashSet<string>(), Now));
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(wt, entry.Cwd, ignoreCase: true);
            Assert.Equal("aea1b65e", entry.WorktreeId);

            var catalog = Catalog();
            Assert.NotNull(catalog.TryGetEntry(root, id, new HashSet<string>(), Now));
            Assert.Null(catalog.LastMissReason);
        }

        [Fact]
        public void A_subfolder_of_a_different_worktree_is_not_used()
        {
            var root = NewProject("ClarionClone");
            var filedIn = Worktree(root, "aea1b65e");
            var other = Worktree(root, "bbbb2222");
            var id = WriteSession(filedIn, Path.Combine(other, "tests"), Path.Combine(other, "tests", "ClarionClone.Tests"));

            var catalog = Catalog();
            Assert.Empty(catalog.List(root, new HashSet<string>(), Now));
            Assert.Null(catalog.TryGetEntry(root, id, new HashSet<string>(), Now));
            Assert.Equal(FolderReason, catalog.LastMissReason);
        }

        // The root's own folder keeps today's rule: a root subfolder is not a resumable cwd, and a
        // worktree subfolder's derived root does not name the root's folder, so the root fallback wins.
        [Fact]
        public void A_transcript_in_the_root_folder_still_resumes_from_the_root()
        {
            var root = NewProject("ClarionClone");
            var wt = Worktree(root, "c8ee0eec");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            // No exact root cwd, so the derived passes run and must not claim the root's folder.
            var id = WriteSession(root, Path.Combine(root, "src"), Path.Combine(wt, "Docking"));

            var entry = Assert.Single(Catalog().List(root, new HashSet<string>(), Now));
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(root, entry.Cwd, ignoreCase: true);
            Assert.Null(entry.WorktreeId);
        }

        [Fact]
        public void A_repo_root_worktree_subfolder_resolves_with_the_repo_root_as_base()
        {
            var repo = NewProject("ClarionAssistant");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var project = Path.Combine(repo, "ClarionAssistant");
            Directory.CreateDirectory(project);
            var wt = Worktree(repo, "9e1bd280");
            var id = WriteSession(wt, project, Path.Combine(wt, "tests"), Path.Combine(wt, "tests", "ClarionClone.Tests"));

            var entry = Assert.Single(Catalog().List(project, new HashSet<string>(), Now));
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(wt, entry.Cwd, ignoreCase: true);
            Assert.Equal(repo, entry.WorktreeBase, ignoreCase: true);
            Assert.Equal("9e1bd280", entry.WorktreeId);

            // Provenance still applies to a derived cwd: a sibling project in the repo is not offered it.
            var sibling = Path.Combine(repo, "Sibling");
            Directory.CreateDirectory(sibling);
            var catalog = Catalog();
            Assert.Null(catalog.TryGetEntry(sibling, id, new HashSet<string>(), Now));
            Assert.Equal("it began in another project's folder of the same repository", catalog.LastMissReason);
        }
    }
}
