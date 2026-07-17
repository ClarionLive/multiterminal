using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task e85eba13 — WorktreeLayout blindness to the current
    /// <c>{repoRoot}/.claude/worktrees/{id}</c> layout. Two halves:
    /// (1) <see cref="WorktreeLayout.DeriveRepoRootFromParent"/> resolves all
    ///     three layouts (it used to carry a pre-.claude copy of the derivation,
    ///     so modern parents resolved to the <c>.claude</c> dir, failed the
    ///     <c>.git</c> check, and silently returned null).
    /// (2) The falsifiability contract downstream: an underivable-but-existing
    ///     parent must surface as a skipped group (scan degrades to partial),
    ///     never vanish into an authoritative-looking ok/0 — and the modern
    ///     layout must actually be SCANNED (the live bug: two real husks on disk
    ///     while /api/worktrees/stranded reported ok/0/0).
    /// </summary>
    public sealed class WorktreeLayoutTests : IDisposable
    {
        private readonly string _baseDir;
        private readonly string _testDbPath;
        private TaskDatabase _db;

        public WorktreeLayoutTests()
        {
            _baseDir = Path.Combine(Path.GetTempPath(), $"mt_wtlayout_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_baseDir);
            _testDbPath = Path.Combine(Path.GetTempPath(), $"mt_wtlayout_db_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _db?.Dispose();
            SQLiteConnection.ClearAllPools(); // release file locks before deletion
            try { if (File.Exists(_testDbPath)) File.Delete(_testDbPath); } catch { }
            try { if (Directory.Exists(_baseDir)) Directory.Delete(_baseDir, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            GC.SuppressFinalize(this);
        }

        // ---- half 1: DeriveRepoRootFromParent across the three layouts ----

        /// <summary>
        /// THE core regression: the current layout. Pre-fix this returned null
        /// (candidate was the .claude dir, which has no .git), which is what
        /// blinded Pass 3 and the stranded scan to every modern-layout parent.
        /// </summary>
        [Fact]
        public void ModernClaudeLayout_ResolvesRepoRoot()
        {
            string repo = MakeGitRepoRoot("repo");
            string parent = Path.Combine(repo, ".claude", "worktrees");
            Directory.CreateDirectory(parent);

            Assert.Equal(repo, WorktreeLayout.DeriveRepoRootFromParent(parent));
        }

        [Fact]
        public void LegacyChildLayout_ResolvesRepoRoot()
        {
            string repo = MakeGitRepoRoot("repo");
            string parent = Path.Combine(repo, "worktrees");
            Directory.CreateDirectory(parent);

            Assert.Equal(repo, WorktreeLayout.DeriveRepoRootFromParent(parent));
        }

        [Fact]
        public void LegacySiblingLayout_ResolvesRepoRoot()
        {
            string repo = MakeGitRepoRoot("repo");
            string parent = Path.Combine(_baseDir, "repo-worktrees");
            Directory.CreateDirectory(parent);

            Assert.Equal(repo, WorktreeLayout.DeriveRepoRootFromParent(parent));
        }

        /// <summary>
        /// A trailing separator must not change the answer (Path.GetFileName on
        /// an untrimmed "...\worktrees\" reads the last segment as "").
        /// </summary>
        [Fact]
        public void ModernLayout_TrailingSeparator_Tolerated()
        {
            string repo = MakeGitRepoRoot("repo");
            string parent = Path.Combine(repo, ".claude", "worktrees");
            Directory.CreateDirectory(parent);

            Assert.Equal(repo, WorktreeLayout.DeriveRepoRootFromParent(parent + Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// The .git-presence guard survives the fix (db4b18c6 cycle 2): a source
        /// tree that merely CONTAINS a dir named worktrees — with no git repo one
        /// OR two levels up — must not derive, or Pass 3 could rmdir inside it.
        /// </summary>
        [Fact]
        public void WorktreesDirInPlainFolder_NoGitAnywhere_ReturnsNull()
        {
            string parent = Path.Combine(_baseDir, "notarepo", "worktrees");
            Directory.CreateDirectory(parent);

            Assert.Null(WorktreeLayout.DeriveRepoRootFromParent(parent));
        }

        [Fact]
        public void ClaudeWorktreesDir_GrandparentNotARepo_ReturnsNull()
        {
            string parent = Path.Combine(_baseDir, "notarepo", ".claude", "worktrees");
            Directory.CreateDirectory(parent);

            Assert.Null(WorktreeLayout.DeriveRepoRootFromParent(parent));
        }

        // ---- half 2: dropped-parent accounting + the scan actually looking ----

        /// <summary>
        /// DeriveWorktreeGroups reports each underivable parent exactly ONCE, no
        /// matter how many DB rows share it (the live DB had 161 rows behind one
        /// blind parent), and still derives the good groups alongside.
        /// </summary>
        [Fact]
        public void DeriveWorktreeGroups_ReportsUnderivableParent_DistinctAndAlongsideGoodGroups()
        {
            string repo = MakeGitRepoRoot("repo");
            string goodParent = Path.Combine(repo, ".claude", "worktrees");
            Directory.CreateDirectory(goodParent);
            string blindParent = Path.Combine(_baseDir, "gone", "worktrees"); // no repo anywhere

            var paths = new List<string>
            {
                Path.Combine(goodParent, "aaaa1111"),
                Path.Combine(goodParent, "bbbb2222"),
                Path.Combine(blindParent, "cccc3333"),
                Path.Combine(blindParent, "dddd4444"),
                Path.Combine(blindParent, "eeee5555"),
            };

            var groups = WorktreeJanitorService.DeriveWorktreeGroups(paths, out var underivable);

            var group = Assert.Single(groups);
            Assert.Equal(repo, group.RepoRoot);
            string dropped = Assert.Single(underivable);
            Assert.Equal(blindParent, dropped);
        }

        /// <summary>
        /// FALSIFIABILITY (negative fixture for the silent-false-zero class): an
        /// existing parent the derivation cannot resolve must degrade the scan to
        /// partial — SkippedGroups counted, parent listed for per-project
        /// attribution. If a future layout change re-blinds the derivation, THIS
        /// is the assertion that goes red instead of the scan lying ok/0/0.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task ScanStranded_UnderivableExistingParent_DegradesToPartial()
        {
            string blindParent = Path.Combine(_baseDir, "mystery", "worktrees"); // exists, no git
            Directory.CreateDirectory(blindParent);
            SeedWorktreeRow("beef0001", Path.Combine(blindParent, "beef0001"));

            var scan = await NewJanitor().ScanStrandedDirsAsync();

            Assert.False(scan.Complete, "underivable existing parent must NOT read as a complete scan");
            Assert.Equal(1, scan.SkippedGroups);
            Assert.Equal(blindParent, Assert.Single(scan.SkippedGroupRepoRoots));
            Assert.Empty(scan.Dirs);
        }

        /// <summary>
        /// Chronic-partial guard: rows whose parent is GONE from disk are benign
        /// (nothing left to scan) — they must not count as skips, or every scan
        /// would be permanently partial on old rows of deleted repos.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task ScanStranded_DeletedParent_NotCountedAsSkip()
        {
            string ghostParent = Path.Combine(_baseDir, "deleted-repo", ".claude", "worktrees"); // never created
            SeedWorktreeRow("dead0002", Path.Combine(ghostParent, "dead0002"));

            var scan = await NewJanitor().ScanStrandedDirsAsync();

            Assert.True(scan.Complete);
            Assert.Equal(0, scan.SkippedGroups);
            Assert.Empty(scan.Dirs);
        }

        /// <summary>
        /// THE live bug, end to end: a modern-layout repo with an empty,
        /// git-unregistered husk (exactly the cf32b08f/2f7280c2 state) must be
        /// REPORTED by the stranded scan. Pre-fix: group dropped at derivation,
        /// scan returned an authoritative-looking ok/0/0.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task ScanStranded_ModernLayout_FindsEmptyUnregisteredHusk()
        {
            string repo = MakeGitRepoRoot("liverepo");
            InitGit(repo);
            string parent = Path.Combine(repo, ".claude", "worktrees");
            string husk = Path.Combine(parent, "deadbeef");
            Directory.CreateDirectory(husk); // empty + never registered with git
            SeedWorktreeRow("feed0003", Path.Combine(parent, "feed0003")); // seeds the group; dir itself absent

            var scan = await NewJanitor().ScanStrandedDirsAsync();

            Assert.True(scan.Complete, "real repo, listable — scan must be complete");
            Assert.Equal(husk, Assert.Single(scan.Dirs));
        }

        // ---- helpers -------------------------------------------------------

        /// <summary>Dir with a .git SUBDIR only — enough for IsLikelyGitRepoRoot.</summary>
        private string MakeGitRepoRoot(string name)
        {
            string repo = Path.Combine(_baseDir, name);
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            return repo;
        }

        private WorktreeJanitorService NewJanitor()
        {
            if (_db == null)
            {
                Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
                _db = new TaskDatabase();
            }

            return new WorktreeJanitorService(_db);
        }

        private void SeedWorktreeRow(string taskId, string worktreePath)
        {
            NewJanitor(); // ensures _db
            _db.SaveWorktreeRecord(taskId, "TestAgent", worktreePath, $"task/{taskId}", isCanonical: true);
        }

        /// <summary>
        /// Upgrade a MakeGitRepoRoot fake into a REAL repo so
        /// `git worktree list` (the registered-set probe) succeeds.
        /// </summary>
        private static void InitGit(string dir)
        {
            RunGit(dir, "init", "-b", "master");
            RunGit(dir, "config", "user.email", "test@mt.local");
            RunGit(dir, "config", "user.name", "MT Test");
            RunGit(dir, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(dir, "README.md"), "seed");
            RunGit(dir, "add", "README.md");
            RunGit(dir, "commit", "-m", "initial commit");
        }

        private static void RunGit(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            Assert.NotNull(proc);
            proc.WaitForExit(15000);
            Assert.Equal(0, proc.ExitCode);
        }
    }
}
