using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 3fd3c9a7: a project registered at a SUBFOLDER of its git repo (ClarionAssistant at
    /// <c>H:\DevLaptop\ClarionAssistant\ClarionAssistant</c>, repo at <c>H:\DevLaptop\ClarionAssistant</c>) keeps
    /// its worktrees at the repo root. A session that ended in one is filed under the repo root's
    /// worktree folder, which the catalog never scanned, so the startup restore skipped Charlie with a
    /// message that listed every possible reason at once. Real temp folders; the repo root is found by
    /// its <c>.git</c> folder.
    /// </summary>
    public sealed class SessionCatalogRepoRootTests : IDisposable
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        private readonly string _base;
        private readonly string _projectsRoot;
        private readonly string _repoRoot;
        private readonly string _projectRoot;
        private readonly string _repoWorktree;

        public SessionCatalogRepoRootTests()
        {
            _base = Path.Combine(Path.GetTempPath(), $"mt_scrr_test_{Guid.NewGuid():N}");
            _projectsRoot = Path.Combine(_base, "projects");
            _repoRoot = Path.Combine(_base, "ClarionAssistant");
            _projectRoot = Path.Combine(_repoRoot, "ClarionAssistant");
            _repoWorktree = Path.Combine(_repoRoot, ".claude", "worktrees", "9e1bd280");
            Directory.CreateDirectory(Path.Combine(_repoRoot, ".git"));
            Directory.CreateDirectory(_projectRoot);
            Directory.CreateDirectory(_repoWorktree);
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

        // A session that started in the project folder and ended in the repo-root worktree, filed where
        // Claude Code files it: under the worktree's own encoded folder.
        private string WriteRepoWorktreeSession()
        {
            var folder = Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(_repoWorktree));
            Directory.CreateDirectory(folder);
            var id = Guid.NewGuid().ToString();
            var path = Path.Combine(folder, id + ".jsonl");
            File.WriteAllText(path, string.Join("\n",
                Line("user", "start", _projectRoot),
                Line("assistant", "ok", _projectRoot),
                Line("assistant", "now in the worktree", _repoWorktree)) + "\n");
            File.SetLastWriteTimeUtc(path, Now.AddHours(-1));
            return id;
        }

        private SessionCatalog Catalog() => new SessionCatalog(_projectsRoot, null, null);

        [Fact]
        public void The_repo_root_is_the_nearest_folder_with_a_git_entry()
        {
            Assert.Equal(_repoRoot, SessionCatalog.FindRepoRoot(_projectRoot), ignoreCase: true);
        }

        [Fact]
        public void A_session_that_ended_in_a_repo_root_worktree_is_listed_and_found()
        {
            var id = WriteRepoWorktreeSession();

            var entry = Assert.Single(Catalog().List(_projectRoot, new HashSet<string>(), Now));
            Assert.Equal(id, entry.SessionId);
            Assert.Equal(_repoWorktree, entry.Cwd, ignoreCase: true);
            Assert.Equal(_repoRoot, entry.WorktreeBase, ignoreCase: true);
            Assert.Equal("9e1bd280", entry.WorktreeId);

            var catalog = Catalog();
            Assert.NotNull(catalog.TryGetEntry(_projectRoot, id, new HashSet<string>(), Now));
            Assert.Null(catalog.LastMissReason);
        }

        [Fact]
        public void Without_the_repo_root_the_session_is_not_found_and_the_reason_says_so()
        {
            var id = WriteRepoWorktreeSession();
            var catalog = Catalog();
            catalog.RepoRootOf = _ => null;   // the pre-fix behaviour: only the project root's worktrees

            Assert.Empty(catalog.List(_projectRoot, new HashSet<string>(), Now));
            Assert.Null(catalog.TryGetEntry(_projectRoot, id, new HashSet<string>(), Now));
            Assert.StartsWith("its transcript was not found in this project's session folders", catalog.LastMissReason);
        }

        [Fact]
        public void The_validated_cwd_accepts_a_repo_root_worktree_only_with_that_repo_root()
        {
            Assert.Equal(_repoWorktree, SessionCatalog.ValidateResumeCwd(_projectRoot, _repoWorktree, _repoRoot), ignoreCase: true);
            Assert.Null(SessionCatalog.ValidateResumeCwd(_projectRoot, _repoWorktree));

            // A "repo root" that is not an ancestor of the project, or is a drive root, widens nothing.
            var sibling = Path.Combine(_base, "Other");
            Assert.Null(SessionCatalog.ValidateResumeCwd(_projectRoot, Path.Combine(sibling, ".claude", "worktrees", "x"), sibling));
            var drive = Path.GetPathRoot(_projectRoot);
            Assert.Null(SessionCatalog.ValidateResumeCwd(_projectRoot, Path.Combine(drive, ".claude", "worktrees", "x"), drive));

            // The worktrees folder itself, and anything else under the repo root, still are not allowed.
            Assert.Null(SessionCatalog.ValidateResumeCwd(_projectRoot, Path.Combine(_repoRoot, ".claude", "worktrees"), _repoRoot));
            Assert.Null(SessionCatalog.ValidateResumeCwd(_projectRoot, Path.Combine(_repoRoot, "src"), _repoRoot));
        }

        // Codex (3fd3c9a7): the repo-root worktrees are shared by every project in the repo, so a sibling
        // project must not be offered a session that began in this one.
        [Fact]
        public void A_sibling_project_in_the_same_repo_is_not_offered_the_session()
        {
            var id = WriteRepoWorktreeSession();
            var sibling = Path.Combine(_repoRoot, "Sibling");
            Directory.CreateDirectory(sibling);

            var catalog = Catalog();
            Assert.Empty(catalog.List(sibling, new HashSet<string>(), Now));
            Assert.Null(catalog.TryGetEntry(sibling, id, new HashSet<string>(), Now));
            Assert.Equal("it began in another project's folder of the same repository", catalog.LastMissReason);

            Assert.NotNull(Catalog().TryGetEntry(_projectRoot, id, new HashSet<string>(), Now));
        }

        // Codex round 2: a registered project nested in another; the parent must not be offered the
        // child's session (first cwd below the parent root, not equal to it).
        [Fact]
        public void A_parent_project_is_not_offered_a_session_that_began_in_a_nested_project()
        {
            // <repo>\Parent is a registered project and <repo>\Parent\Child another; both below the repo root.
            var nestedRepo = Path.Combine(_base, "Nested");
            var parentProject = Path.Combine(nestedRepo, "Parent");
            var childProject = Path.Combine(parentProject, "Child");
            var worktree = Path.Combine(nestedRepo, ".claude", "worktrees", "abcd1234");
            Directory.CreateDirectory(Path.Combine(nestedRepo, ".git"));
            Directory.CreateDirectory(childProject);
            Directory.CreateDirectory(worktree);
            var folder = Path.Combine(_projectsRoot, SessionCatalog.EncodeProjectPath(worktree));
            Directory.CreateDirectory(folder);
            var childId = Guid.NewGuid().ToString();
            var path = Path.Combine(folder, childId + ".jsonl");
            File.WriteAllText(path, string.Join("\n",
                Line("user", "start", childProject),
                Line("assistant", "ok", childProject),
                Line("assistant", "now in the worktree", worktree)) + "\n");
            File.SetLastWriteTimeUtc(path, Now.AddHours(-1));

            var fresh = Catalog();
            Assert.Null(fresh.TryGetEntry(parentProject, childId, new HashSet<string>(), Now));
            Assert.Equal("it began in another project's folder of the same repository", fresh.LastMissReason);
            Assert.NotNull(Catalog().TryGetEntry(childProject, childId, new HashSet<string>(), Now));
        }

        [Fact]
        public void A_live_session_is_reported_as_already_open()
        {
            var id = WriteRepoWorktreeSession();
            var catalog = Catalog();
            Assert.Null(catalog.TryGetEntry(_projectRoot, id, new HashSet<string> { id }, Now));
            Assert.Equal("it is already open in this MultiTerminal", catalog.LastMissReason);
        }

        [Fact]
        public void The_helper_resume_skip_message_carries_the_catalog_reason()
        {
            var helper = new OpenHelper { TerminalName = "Quinn", SessionId = Guid.NewGuid().ToString(), Kind = "claude" };
            var check = HelperResume.Check(helper, null, Array.Empty<PaneSessionState>(), _projectRoot, _ => null, Directory.Exists,
                catalogMissReason: "its transcript was not found in this project's session folders");
            Assert.False(check.CanResume);
            Assert.Equal("its session cannot be resumed: its transcript was not found in this project's session folders", check.SkipReason);
        }
    }
}
