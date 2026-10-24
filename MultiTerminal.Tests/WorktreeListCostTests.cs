using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 47d8473b: MT idled at about half a core, nearly all kernel time from starting git.exe.
    /// Every terminal's 10s dirty poll listed the repo's worktrees, and each list started one git
    /// per worktree for the membership check plus one per worktree for the dirty count. On a repo
    /// with 59 worktrees that was about 77 processes per terminal per tick.
    /// </summary>
    public sealed class WorktreeListCostTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly TaskDatabase _db;
        private readonly string _root;
        private readonly string _repo;
        private readonly string _worktree;

        public WorktreeListCostTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_wtlist_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _dbPath + ".msg");
            _db = new TaskDatabase();

            _root = Path.Combine(Path.GetTempPath(), $"mt_wtlist_{Guid.NewGuid():N}");
            _repo = Path.Combine(_root, "repo");
            _worktree = Path.Combine(_repo, ".claude", "worktrees", "abcd1234");
            Directory.CreateDirectory(_repo);
            Git(_repo, "init", "-q");
            Git(_repo, "config", "user.email", "t@example.com");
            Git(_repo, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(_repo, "a.txt"), "a");
            Git(_repo, "add", "a.txt");
            Git(_repo, "commit", "-q", "-m", "init");
            Git(_repo, "worktree", "add", "-q", "-b", "task/abcd1234", _worktree);
        }

        public void Dispose()
        {
            _db.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + ".msg", _dbPath + ".msg-wal", _dbPath + ".msg-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }

            try
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort; a temp folder left behind is harmless.
            }

            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr}");
            return stdout;
        }

        private static string GitCommonDir(string dir)
        {
            string raw = Git(dir, "rev-parse", "--git-common-dir").Trim();
            return Path.GetFullPath(Path.Combine(dir, raw)).Replace('\\', '/').TrimEnd('/');
        }

        [Fact]
        public void The_common_dir_read_from_files_is_what_git_reports_for_the_main_checkout_and_a_worktree()
        {
            Assert.Equal(GitCommonDir(_repo), WorktreeListService.TryReadCommonGitDir(_repo), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(GitCommonDir(_worktree), WorktreeListService.TryReadCommonGitDir(_worktree), StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_folder_that_is_not_a_worktree_root_is_left_to_git()
        {
            string sub = Path.Combine(_repo, "sub");
            Directory.CreateDirectory(sub);

            Assert.Null(WorktreeListService.TryReadCommonGitDir(sub));
        }

        [Fact]
        public void A_gitdir_pointing_at_a_network_share_is_rejected_not_opened()
        {
            string fake = Path.Combine(_root, "fake");
            Directory.CreateDirectory(fake);
            File.WriteAllText(Path.Combine(fake, ".git"), "gitdir: \\\\evil-host\\share\\repo\\.git\\worktrees\\x");

            Assert.Equal(string.Empty, WorktreeListService.TryReadCommonGitDir(fake));
        }

        [Fact]
        public void A_gitdir_outside_the_common_dirs_worktrees_folder_is_left_to_git()
        {
            // A real worktree's admin dir, copied elsewhere: its commondir still names the real
            // repository, but it is not where `git worktree add` puts one.
            string realAdmin = Path.Combine(_repo, ".git", "worktrees", "abcd1234");
            string elsewhere = Path.Combine(_root, "elsewhere-admin");
            Directory.CreateDirectory(elsewhere);
            File.WriteAllText(Path.Combine(elsewhere, "commondir"), Path.Combine(_repo, ".git"));
            Assert.True(Directory.Exists(realAdmin));

            string fake = Path.Combine(_root, "claims-the-repo");
            Directory.CreateDirectory(fake);
            File.WriteAllText(Path.Combine(fake, ".git"), "gitdir: " + elsewhere);

            Assert.Null(WorktreeListService.TryReadCommonGitDir(fake));
        }

        [Fact]
        public void A_malformed_dot_git_file_is_left_to_git()
        {
            string fake = Path.Combine(_root, "malformed");
            Directory.CreateDirectory(fake);
            File.WriteAllText(Path.Combine(fake, ".git"), "not a gitdir line");

            Assert.Null(WorktreeListService.TryReadCommonGitDir(fake));
        }

        [Fact]
        public async Task The_terminal_poll_shares_a_recent_list_and_a_fresh_caller_does_not()
        {
            using var broker = new MessageBroker();
            var svc = new WorktreeListService(_db, broker);

            var first = await svc.GetWorktreesForRepoAsync(_repo, TimeSpan.FromMinutes(5));
            Assert.Equal(2, first.Count);

            // A third worktree appears. The poll, within its age, keeps the shared result; a caller
            // asking for a fresh list (the Git tab, the REST route) sees it at once.
            string third = Path.Combine(_repo, ".claude", "worktrees", "ef015678");
            Git(_repo, "worktree", "add", "-q", "-b", "task/ef015678", third);

            Assert.Equal(2, (await svc.GetWorktreesForRepoAsync(_repo, TimeSpan.FromMinutes(5))).Count);
            Assert.Equal(3, (await svc.GetWorktreesForRepoAsync(_repo)).Count);

            // The fresh read refreshed the shared one, and a poll from another worktree of the same
            // repo is handed it: a fourth worktree added now is not seen, so the answer came from
            // the shared list rather than a new read (which would count 4).
            string fourth = Path.Combine(_repo, ".claude", "worktrees", "ef019999");
            Git(_repo, "worktree", "add", "-q", "-b", "task/ef019999", fourth);
            Assert.Equal(3, (await svc.GetWorktreesForRepoAsync(_worktree, TimeSpan.FromMinutes(5))).Count);
        }

        [Fact]
        public async Task A_caller_cannot_change_what_the_next_caller_is_handed()
        {
            using var broker = new MessageBroker();
            var svc = new WorktreeListService(_db, broker);

            var mine = await svc.GetWorktreesForRepoAsync(_repo, TimeSpan.FromMinutes(5));
            mine[0].Branch = "tampered";
            mine.Clear();

            var next = await svc.GetWorktreesForRepoAsync(_repo, TimeSpan.FromMinutes(5));
            Assert.Equal(2, next.Count);
            Assert.NotEqual("tampered", next[0].Branch);
            Assert.True(next.Single(e => e.IsMain).Path.Length > 0);
        }
    }
}
