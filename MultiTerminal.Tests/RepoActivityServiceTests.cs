#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="RepoActivityService"/> (task df58b35d, GitHub #53): when a watched project is checked,
    /// what counts as new, and what the summary says. Runs against the REAL <see cref="TaskDatabase"/>
    /// on a temp SQLite file, with a fake host, a scripted fake GitHub client, fake git remotes and a
    /// clock the test moves. The tick is driven through <c>TickAsync</c>, never the timer, and every
    /// await is bounded so a broken service fails instead of hanging the suite.
    /// </summary>
    public sealed class RepoActivityServiceTests : IDisposable
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
        private static readonly DateTimeOffset T0 = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

        private readonly string _testDbPath;
        private readonly string _msgDbPath;
        private readonly string? _envBefore;
        private readonly TaskDatabase _db;
        private readonly FakeHost _host = new();
        private readonly FakeClient _client = new();
        private readonly ConcurrentDictionary<string, IReadOnlyList<GitRemoteRepo>> _remotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _remoteReads = new();
        private readonly ConcurrentQueue<string> _log = new();
        private DateTimeOffset _now = T0;

        public RepoActivityServiceTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_repoactivity_{Guid.NewGuid():N}.db");
            _msgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_repoactivity_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _msgDbPath);
            _envBefore = Environment.GetEnvironmentVariable("MULTITERMINAL_REPO_WATCH");
            Environment.SetEnvironmentVariable("MULTITERMINAL_REPO_WATCH", null);
            _db = new TaskDatabase();
        }

        public void Dispose()
        {
            _db.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var basePath in new[] { _testDbPath, _msgDbPath })
            {
                foreach (var p in new[] { basePath, basePath + "-wal", basePath + "-shm" })
                {
                    if (File.Exists(p)) File.Delete(p);
                }
            }

            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_REPO_WATCH", _envBefore);
        }

        // ── What counts as new ───────────────────────────────────────────────

        [Fact]
        public async Task The_first_check_sets_the_seen_mark_so_nothing_is_new()
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => Ok(Item(5), Item(3, pr: true));
            using var svc = NewService();

            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            var summary = svc.GetSummary(project.Id);
            Assert.True(summary.Watching);
            Assert.Equal(RepoActivityState.Ok, summary.State);
            Assert.Equal(0, summary.NewCount);
            Assert.Equal(1, summary.OpenIssues);
            Assert.Equal(1, summary.OpenPullRequests);
            Assert.Equal(T0.UtcDateTime, summary.LastCheckedUtc);
            Assert.Equal(5, _db.GetRepoWatchState("p1", "owner/repo")!.SeenMaxNumber);
        }

        [Fact]
        public async Task An_empty_repository_sets_the_seen_mark_to_zero()
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => Ok();
            using var svc = NewService();

            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            Assert.Equal(0, _db.GetRepoWatchState("p1", "owner/repo")!.SeenMaxNumber);

            _client.Respond = (_, _) => Ok(Item(1));
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            Assert.Equal(1, svc.GetSummary(project.Id).NewCount);
        }

        [Fact]
        public async Task A_later_check_with_higher_numbers_reports_them_new_and_first()
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => Ok(Item(3), Item(2));
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            _client.Respond = (_, _) => Ok(Item(8), Item(7, pr: true), Item(3), Item(2));
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            var summary = svc.GetSummary(project.Id);
            Assert.Equal(2, summary.NewCount);
            var items = summary.Repos.Single().Items;
            Assert.Equal(new long[] { 8, 7, 3, 2 }, items.Select(e => e.Item.Number));
            Assert.Equal(new[] { true, true, false, false }, items.Select(e => e.IsNew));
        }

        [Fact]
        public async Task MarkAllSeen_clears_new_and_raises_the_event()
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => Ok(Item(3));
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            _client.Respond = (_, _) => Ok(Item(9), Item(3));
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            Assert.Equal(1, svc.GetSummary(project.Id).NewCount);

            var raised = new ConcurrentQueue<string>();
            svc.RepoActivityChanged += (_, e) => raised.Enqueue(e.ProjectId);
            svc.MarkAllSeen(project.Id);

            Assert.Equal(0, svc.GetSummary(project.Id).NewCount);
            Assert.Equal(9, _db.GetRepoWatchState("p1", "owner/repo")!.SeenMaxNumber);
            Assert.Equal(new[] { "p1" }, raised);
        }

        [Fact]
        public async Task Not_modified_keeps_the_stored_items_and_sends_the_stored_etag()
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => OkWithETag("\"e1\"", Item(4), Item(2));
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            _client.Respond = (_, etag) => new RepoIssuesResult { Status = RepoIssuesStatus.NotModified, ETag = etag };
            _now = T0.AddMinutes(1);
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            Assert.Equal(new string?[] { null, "\"e1\"" }, _client.Calls.Select(c => c.ETag));
            var summary = svc.GetSummary(project.Id);
            Assert.Equal(RepoActivityState.Ok, summary.State);
            Assert.Equal(new long[] { 4, 2 }, summary.Repos.Single().Items.Select(e => e.Item.Number));
            Assert.Equal(_now.UtcDateTime, summary.LastCheckedUtc);
        }

        [Fact]
        public async Task A_null_html_url_falls_back_to_the_issue_or_pull_page()
        {
            var project = AddProject("p1", repos: "Owner/Repo");
            _client.Respond = (_, _) => Ok(
                Item(10, pr: true),
                Item(9),
                Item(8, url: "https://github.com/Owner/Repo/issues/8"));
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            var urls = svc.GetSummary(project.Id).Repos.Single().Items.Select(e => e.Url).ToList();
            Assert.Equal(
                new[]
                {
                    "https://github.com/Owner/Repo/pull/10",
                    "https://github.com/Owner/Repo/issues/9",
                    "https://github.com/Owner/Repo/issues/8",
                },
                urls);
        }

        [Fact]
        public async Task A_full_page_is_reported_as_truncated()
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => Ok(Enumerable.Range(1, GitHubRestClient.PageSize).Select(n => Item(n)).ToArray());
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            var summary = svc.GetSummary(project.Id);
            Assert.True(summary.Truncated);
            Assert.True(summary.Repos.Single().Truncated);
        }

        // ── When ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Interval_zero_is_never_checked_on_the_tick_but_is_on_launch()
        {
            var project = AddProject("p1", repos: "owner/repo", interval: 0, token: "tok");
            using var svc = NewService();

            await svc.TickAsync().WaitAsync(Bound);
            _now = T0.AddHours(5);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Empty(_client.Calls);

            await svc.ScheduleLaunchCheck(project.Id).WaitAsync(Bound);
            Assert.Single(_client.Calls);
        }

        [Fact]
        public async Task The_tick_respects_the_interval()
        {
            AddProject("p1", repos: "owner/repo", interval: 5, token: "tok");
            using var svc = NewService();

            await svc.TickAsync().WaitAsync(Bound);
            Assert.Single(_client.Calls);

            _now = T0.AddMinutes(4);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Single(_client.Calls);

            _now = T0.AddMinutes(5);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Equal(2, _client.Calls.Count);
            Assert.All(_client.Calls, c => Assert.Equal("tok", c.Token));
        }

        [Fact]
        public async Task Without_a_token_the_tick_waits_at_least_fifteen_minutes()
        {
            AddProject("p1", repos: "owner/repo", interval: 5, token: null);
            using var svc = NewService();

            await svc.TickAsync().WaitAsync(Bound);
            Assert.Single(_client.Calls);
            Assert.Null(_client.Calls.Single().Token);

            _now = T0.AddMinutes(14);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Single(_client.Calls);

            _now = T0.AddMinutes(15);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Equal(2, _client.Calls.Count);
        }

        [Fact]
        public async Task CheckNow_ignores_the_interval_and_the_unauthenticated_floor()
        {
            var project = AddProject("p1", repos: "owner/repo", interval: 60, token: null);
            using var svc = NewService();

            await svc.TickAsync().WaitAsync(Bound);
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            Assert.Equal(2, _client.Calls.Count);
        }

        [Fact]
        public async Task The_tick_skips_a_project_with_no_open_pane()
        {
            var project = AddProject("p1", repos: "owner/repo", interval: 5, token: "tok");
            _host.OpenPanes.Remove(project.Id);
            using var svc = NewService();

            await svc.TickAsync().WaitAsync(Bound);
            Assert.Empty(_client.Calls);

            _host.OpenPanes.Add(project.Id);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Single(_client.Calls);
        }

        [Fact]
        public async Task The_startup_sweep_checks_every_watched_project_with_or_without_a_pane()
        {
            AddProject("p1", repos: "a/one", interval: 0);
            var p2 = AddProject("p2", repos: "b/two", interval: 15);
            _host.OpenPanes.Remove(p2.Id);
            AddProject("p3", repos: "c/three", enabled: false);
            using var svc = NewService();

            await svc.StartupSweepAsync().WaitAsync(Bound);

            Assert.Equal(new[] { "a/one", "b/two" }, _client.Calls.Select(c => c.Repo).OrderBy(r => r, StringComparer.Ordinal));
        }

        [Fact]
        public async Task A_rate_limited_repository_is_skipped_until_retry_after_then_resumes()
        {
            var project = AddProject("p1", repos: "owner/repo", interval: 5, token: "tok");
            _client.Respond = (_, _) => new RepoIssuesResult
            {
                Status = RepoIssuesStatus.RateLimited,
                RetryAfterUtc = T0.AddMinutes(10).UtcDateTime,
                Message = "GitHub's rate limit was reached.",
            };
            using var svc = NewService();

            await svc.TickAsync().WaitAsync(Bound);
            Assert.Single(_client.Calls);
            var limited = svc.GetSummary(project.Id);
            Assert.Equal(RepoActivityState.RateLimited, limited.State);
            Assert.Contains("rate limit", limited.Reason);

            _client.Respond = (_, _) => Ok(Item(1));
            _now = T0.AddMinutes(6); // interval due, backoff not over
            await svc.TickAsync().WaitAsync(Bound);
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            await svc.ScheduleLaunchCheck(project.Id).WaitAsync(Bound);
            Assert.Single(_client.Calls);

            _now = T0.AddMinutes(11);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Equal(2, _client.Calls.Count);
            Assert.Equal(RepoActivityState.Ok, svc.GetSummary(project.Id).State);
        }

        [Fact]
        public async Task Two_concurrent_checks_of_one_project_make_one_call()
        {
            var project = AddProject("p1", repos: "owner/repo");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.RespondAsync = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Bound);
                return Ok(Item(1));
            };
            using var svc = NewService();

            var first = svc.CheckNowAsync(project.Id);
            await entered.Task.WaitAsync(Bound);
            Assert.Equal(RepoActivityState.Checking, svc.GetSummary(project.Id).State);

            var second = svc.CheckNowAsync(project.Id);
            var launch = svc.ScheduleLaunchCheck(project.Id);
            Assert.False(second.IsCompleted); // joined the running check rather than starting or skipping
            release.SetResult();
            await Task.WhenAll(first, second, launch).WaitAsync(Bound);

            Assert.Single(_client.Calls);
            Assert.Equal(RepoActivityState.Ok, svc.GetSummary(project.Id).State);
        }

        [Fact]
        public async Task A_slow_project_does_not_hold_up_another()
        {
            AddProject("slow", repos: "a/slow");
            var fast = AddProject("fast", repos: "b/fast");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.RespondAsync = async (repo, _) =>
            {
                if (repo == "a/slow") await release.Task.WaitAsync(Bound);
                return Ok(Item(1));
            };
            using var svc = NewService();

            var slowCheck = svc.CheckNowAsync("slow");
            await svc.CheckNowAsync(fast.Id).WaitAsync(Bound);
            Assert.False(slowCheck.IsCompleted);
            Assert.Equal(RepoActivityState.Ok, svc.GetSummary(fast.Id).State);

            release.SetResult();
            await slowCheck.WaitAsync(Bound);
        }

        // ── Which repositories ───────────────────────────────────────────────

        [Fact]
        public async Task Repos_in_the_settings_override_the_remotes()
        {
            var project = AddProject("p1", repos: "a/listed");
            _remotes[project.Path] = new[] { Remote("origin", "c/remote") };
            using var svc = NewService();

            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            Assert.Equal(new[] { "a/listed" }, _client.Calls.Select(c => c.Repo));
            Assert.Empty(_remoteReads);
        }

        [Fact]
        public async Task Remotes_are_used_when_none_are_listed_and_de_duplicated()
        {
            var project = AddProject("p1", repos: null);
            _remotes[project.Path] = new[] { Remote("origin", "x/y"), Remote("upstream", "X/Y"), Remote("other", "z/w") };
            using var svc = NewService();

            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            Assert.Equal(new[] { "x/y", "z/w" }, _client.Calls.Select(c => c.Repo));
            Assert.Equal(new[] { "x/y", "z/w" }, svc.GetSummary(project.Id).Repos.Select(r => r.FullName));
        }

        [Fact]
        public async Task The_git_url_is_the_last_fallback()
        {
            var project = AddProject("p1", repos: null);
            project.GitRepoUrl = "https://github.com/o/r.git";
            using var svc = NewService();

            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            Assert.Equal(new[] { "o/r" }, _client.Calls.Select(c => c.Repo));
            Assert.Single(_remoteReads); // the remotes were asked first
        }

        [Fact]
        public async Task No_repository_anywhere_is_NoRepos()
        {
            var project = AddProject("p1", repos: null);
            project.GitRepoUrl = "https://gitlab.com/o/r.git";
            using var svc = NewService();

            Assert.Equal(RepoActivityState.NeverChecked, svc.GetSummary(project.Id).State);
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            Assert.Empty(_client.Calls);
            var summary = svc.GetSummary(project.Id);
            Assert.Equal(RepoActivityState.NoRepos, summary.State);
            Assert.False(string.IsNullOrWhiteSpace(summary.Reason));
            Assert.Empty(summary.Repos);
        }

        // ── The Repositories setting changes (pipeline Run 1) ───────────────

        [Fact]
        public async Task A_cleared_list_stops_showing_its_repo_and_a_settings_check_resolves_the_remotes()
        {
            var project = AddProject("p1", repos: "acme/a");
            _remotes[project.Path] = new[] { Remote("origin", "x/y") };
            _client.Respond = (_, _) => Ok(Item(1));
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            Assert.Equal(new[] { "acme/a" }, svc.GetSummary(project.Id).Repos.Select(r => r.FullName));

            project.RepoWatchRepos = null;

            // The resolution was made under the old list: not shown, and not Ok, so the badge says not checked.
            var stale = svc.GetSummary(project.Id);
            Assert.Empty(stale.Repos);
            Assert.Equal(RepoActivityState.NeverChecked, stale.State);

            await svc.ScheduleSettingsCheck(project.Id).WaitAsync(Bound);

            Assert.Equal(new[] { "acme/a", "x/y" }, _client.Calls.Select(c => c.Repo));
            var summary = svc.GetSummary(project.Id);
            Assert.Equal(new[] { "x/y" }, summary.Repos.Select(r => r.FullName));
            Assert.Equal(RepoActivityState.Ok, summary.State);
            Assert.Null(_db.GetRepoWatchState("p1", "acme/a"));
        }

        [Fact]
        public async Task A_changed_list_shows_only_the_new_repo_and_forgets_the_old_one_once_checked()
        {
            var project = AddProject("p1", repos: "acme/a");
            _client.Respond = (_, _) => Ok(Item(1));
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            project.RepoWatchRepos = "acme/b";
            var before = svc.GetSummary(project.Id);
            Assert.Equal(new[] { "acme/b" }, before.Repos.Select(r => r.FullName));
            Assert.Equal(RepoActivityState.NeverChecked, before.State);

            await svc.ScheduleSettingsCheck(project.Id).WaitAsync(Bound);

            Assert.Equal(RepoActivityState.Ok, svc.GetSummary(project.Id).State);
            Assert.Null(_db.GetRepoWatchState("p1", "acme/a"));
            Assert.NotNull(_db.GetRepoWatchState("p1", "acme/b"));
        }

        [Fact]
        public async Task After_a_restart_with_no_list_only_the_last_resolved_repos_are_shown()
        {
            // List acme/a, then clear it: the next check resolves the remotes and forgets acme/a, so a new
            // service over the same database (a restart, before any check) does not bring it back.
            var project = AddProject("p1", repos: "acme/a");
            _remotes[project.Path] = new[] { Remote("origin", "x/y") };
            using (var first = NewService())
            {
                await first.CheckNowAsync(project.Id).WaitAsync(Bound);
                project.RepoWatchRepos = null;
                await first.ScheduleSettingsCheck(project.Id).WaitAsync(Bound);
            }

            using var restarted = NewService();
            Assert.Equal(new[] { "x/y" }, restarted.GetSummary(project.Id).Repos.Select(r => r.FullName));
        }

        [Fact]
        public async Task After_a_restart_a_listed_repo_is_shown_and_a_removed_one_is_not()
        {
            var project = AddProject("p1", repos: "acme/a");
            using (var first = NewService())
            {
                await first.CheckNowAsync(project.Id).WaitAsync(Bound);
            }

            project.RepoWatchRepos = "acme/b";
            using var restarted = NewService();
            var summary = restarted.GetSummary(project.Id);
            Assert.Equal(new[] { "acme/b" }, summary.Repos.Select(r => r.FullName));
            Assert.Equal(RepoActivityState.NeverChecked, summary.State);
        }

        [Fact]
        public async Task An_empty_remotes_read_forgets_nothing()
        {
            // GitRemoteReader answers a failed or timed-out read with an empty list.
            var project = AddProject("p1", repos: "acme/a");
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            project.RepoWatchRepos = null;
            project.GitRepoUrl = "https://gitlab.com/o/r.git";
            await svc.ScheduleSettingsCheck(project.Id).WaitAsync(Bound);

            Assert.Equal(RepoActivityState.NoRepos, svc.GetSummary(project.Id).State);
            Assert.NotNull(_db.GetRepoWatchState("p1", "acme/a"));
        }

        [Fact]
        public async Task The_git_url_fallback_after_an_empty_remotes_read_forgets_nothing()
        {
            var project = AddProject("p1", repos: "acme/a");
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            project.RepoWatchRepos = null;
            project.GitRepoUrl = "https://github.com/o/r.git";
            await svc.ScheduleSettingsCheck(project.Id).WaitAsync(Bound);

            Assert.Equal(new[] { "o/r" }, svc.GetSummary(project.Id).Repos.Select(r => r.FullName));
            Assert.NotNull(_db.GetRepoWatchState("p1", "acme/a"));
        }

        [Fact]
        public async Task A_settings_change_during_a_check_queues_a_check_under_the_new_settings()
        {
            var project = AddProject("p1", repos: "acme/a");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.RespondAsync = async (repo, _) =>
            {
                if (repo == "acme/a")
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(Bound);
                }

                return Ok(Item(1));
            };
            using var svc = NewService();

            var running = svc.CheckNowAsync(project.Id);
            await entered.Task.WaitAsync(Bound);
            project.RepoWatchRepos = "acme/b";
            var settingsCheck = svc.ScheduleSettingsCheck(project.Id);
            Assert.False(settingsCheck.IsCompleted);

            release.SetResult();
            await running.WaitAsync(Bound);
            await settingsCheck.WaitAsync(Bound);

            Assert.Equal(new[] { "acme/a", "acme/b" }, _client.Calls.Select(c => c.Repo));
            var summary = svc.GetSummary(project.Id);
            Assert.Equal(new[] { "acme/b" }, summary.Repos.Select(r => r.FullName));
            Assert.Equal(RepoActivityState.Ok, summary.State);
        }

        // ── Failures ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData(RepoIssuesStatus.NotFound, RepoActivityState.NoAccess, "no_access")]
        [InlineData(RepoIssuesStatus.Unauthorized, RepoActivityState.NoAccess, "no_access")]
        [InlineData(RepoIssuesStatus.Failed, RepoActivityState.Error, "error")]
        public async Task A_failure_sets_the_state_and_reason_and_keeps_the_last_items(
            RepoIssuesStatus status, RepoActivityState expected, string storedStatus)
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => OkWithETag("\"e1\"", Item(2));
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            _client.Respond = (_, _) => new RepoIssuesResult { Status = status, Message = "GitHub said no (test)." };
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            var summary = svc.GetSummary(project.Id);
            Assert.Equal(expected, summary.State);
            Assert.Equal("owner/repo: GitHub said no (test).", summary.Reason);
            Assert.Equal(new long[] { 2 }, summary.Repos.Single().Items.Select(e => e.Item.Number));
            var stored = _db.GetRepoWatchState("p1", "owner/repo")!;
            Assert.Equal(storedStatus, stored.LastStatus);
            Assert.Equal("\"e1\"", stored.ETag);
        }

        [Fact]
        public async Task One_ok_repository_and_one_unchecked_is_Ok_but_a_failure_is_worst()
        {
            var project = AddProject("p1", repos: "a/good, b/other");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var otherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _client.RespondAsync = async (repo, _) =>
            {
                if (repo == "b/other")
                {
                    otherEntered.TrySetResult();
                    await release.Task.WaitAsync(Bound);
                    return new RepoIssuesResult { Status = RepoIssuesStatus.Failed, Message = "boom" };
                }

                return Ok(Item(1));
            };
            using var svc = NewService();

            var check = svc.CheckNowAsync(project.Id);
            await otherEntered.Task.WaitAsync(Bound);
            var midway = svc.GetSummary(project.Id);
            Assert.Equal(new[] { RepoActivityState.Ok, RepoActivityState.Checking }, midway.Repos.Select(r => r.State));
            Assert.Equal(RepoActivityState.Ok, midway.State);

            release.SetResult();
            await check.WaitAsync(Bound);
            Assert.Equal(RepoActivityState.Error, svc.GetSummary(project.Id).State);
        }

        [Fact]
        public async Task A_throwing_client_is_logged_as_an_error_and_never_logs_the_token()
        {
            var project = AddProject("p1", repos: "owner/repo", token: "ghp_SECRET_TOKEN_VALUE");
            _client.Respond = (_, _) => throw new InvalidOperationException("client blew up");
            using var svc = NewService();

            await svc.ScheduleLaunchCheck(project.Id).WaitAsync(Bound);

            Assert.Equal(RepoActivityState.Error, svc.GetSummary(project.Id).State);
            Assert.Contains(_log, l => l.Contains("client blew up", StringComparison.Ordinal));
            Assert.Equal("ghp_SECRET_TOKEN_VALUE", _client.Calls.Single().Token);
            Assert.DoesNotContain(_log, l => l.Contains("SECRET", StringComparison.Ordinal));
        }

        // ── Events, switches ─────────────────────────────────────────────────

        [Fact]
        public async Task A_check_raises_the_event_when_it_starts_and_when_it_ends()
        {
            var project = AddProject("p1", repos: "owner/repo");
            var states = new ConcurrentQueue<RepoActivityState>();
            using var svc = NewService();
            svc.RepoActivityChanged += (_, e) => states.Enqueue(svc.GetSummary(e.ProjectId).State);

            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            Assert.Equal(new[] { RepoActivityState.Checking, RepoActivityState.Ok }, states);
        }

        [Fact]
        public async Task A_project_with_watching_off_is_Disabled_and_not_checked()
        {
            var project = AddProject("p1", repos: "owner/repo", enabled: false);
            using var svc = NewService();

            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            await svc.ScheduleLaunchCheck(project.Id).WaitAsync(Bound);

            Assert.Empty(_client.Calls);
            var summary = svc.GetSummary(project.Id);
            Assert.False(summary.Watching);
            Assert.Equal(RepoActivityState.Disabled, summary.State);
        }

        [Fact]
        public async Task The_env_switch_turns_the_whole_service_off()
        {
            var project = AddProject("p1", repos: "owner/repo");
            Environment.SetEnvironmentVariable("MULTITERMINAL_REPO_WATCH", "Off");
            using var svc = NewService();

            Assert.True(RepoActivityService.IsDisabled());
            svc.Start();
            Assert.False(svc.IsRunning);
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);
            await svc.ScheduleLaunchCheck(project.Id).WaitAsync(Bound);
            Assert.Empty(_client.Calls);
            Assert.Equal(RepoActivityState.Disabled, svc.GetSummary(project.Id).State);
            Assert.Contains("MULTITERMINAL_REPO_WATCH", svc.GetSummary(project.Id).Reason);
        }

        [Fact]
        public void Start_is_idempotent_and_Dispose_stops_it()
        {
            // No watched projects, so the startup sweep has nothing to do in the background.
            using var svc = NewService();
            Assert.False(RepoActivityService.IsDisabled());

            svc.Start();
            svc.Start();
            Assert.True(svc.IsRunning);

            svc.Dispose();
            Assert.False(svc.IsRunning);
        }

        // ── Forgetting projects no longer watched (d270f9dc finding 8) ───────

        [Fact]
        public async Task A_tick_forgets_a_project_whose_watching_was_turned_off()
        {
            var keep = AddProject("p1", repos: "owner/keep");
            var drop = AddProject("p2", repos: "owner/drop");
            using var svc = NewService();
            await svc.CheckNowAsync(keep.Id).WaitAsync(Bound);
            await svc.CheckNowAsync(drop.Id).WaitAsync(Bound);
            Assert.Equal(2, svc.RememberedCounts().Projects);

            drop.RepoWatchEnabled = false;
            await svc.TickAsync().WaitAsync(Bound);

            Assert.Equal(1, svc.RememberedCounts().Projects);
        }

        [Fact]
        public async Task A_tick_forgets_a_deleted_project()
        {
            var gone = AddProject("p1", repos: "owner/repo");
            using var svc = NewService();
            await svc.CheckNowAsync(gone.Id).WaitAsync(Bound);
            Assert.Equal(1, svc.RememberedCounts().Projects);

            _host.Projects.TryRemove(gone.Id, out _);
            await svc.TickAsync().WaitAsync(Bound);

            Assert.Equal(0, svc.RememberedCounts().Projects);
        }

        [Fact]
        public async Task An_expired_backoff_is_forgotten_and_a_live_one_is_kept()
        {
            var project = AddProject("p1", repos: "owner/repo");
            _client.Respond = (_, _) => new RepoIssuesResult
            {
                Status = RepoIssuesStatus.RateLimited,
                RetryAfterUtc = T0.UtcDateTime.AddMinutes(10),
                Message = "rate limited",
            };
            using var svc = NewService();
            await svc.CheckNowAsync(project.Id).WaitAsync(Bound);

            // Turned off inside the rate limit: the backoff must survive so turning it on again cannot
            // call GitHub before the reset.
            project.RepoWatchEnabled = false;
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Equal(1, svc.RememberedCounts().Backoffs);

            _now = T0.AddMinutes(11);
            await svc.TickAsync().WaitAsync(Bound);
            Assert.Equal(0, svc.RememberedCounts().Backoffs);
        }

        [Fact]
        public async Task A_settings_change_on_an_unwatched_project_calls_nothing()
        {
            var project = AddProject("p1", repos: "owner/repo", enabled: false);
            using var svc = NewService();

            await svc.ScheduleSettingsCheck(project.Id).WaitAsync(Bound);

            Assert.Empty(_client.Calls);
            Assert.Equal(0, svc.RememberedCounts().Projects);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private RepoActivityService NewService()
            => new(_host, _db, _client, ReadRemotesAsync, () => _now, _log.Enqueue);

        private Task<IReadOnlyList<GitRemoteRepo>> ReadRemotesAsync(string folder, CancellationToken ct)
        {
            _remoteReads.Enqueue(folder);
            return Task.FromResult(_remotes.TryGetValue(folder, out var list) ? list : (IReadOnlyList<GitRemoteRepo>)Array.Empty<GitRemoteRepo>());
        }

        private Project AddProject(string id, string? repos, int? interval = 15, string? token = "tok", bool enabled = true)
        {
            var project = new Project
            {
                Id = id,
                Name = "Project " + id,
                Path = @"C:\fake\" + id,
                RepoWatchEnabled = enabled,
                RepoWatchIntervalMin = interval,
                RepoWatchRepos = repos,
            };
            _host.Projects[id] = project;
            _host.OpenPanes.Add(id);
            if (token != null) _host.Tokens[id] = token;
            return project;
        }

        private static GitRemoteRepo Remote(string name, string fullName)
        {
            Assert.True(GitHubRepoRef.TryParseFullName(fullName, out var repo));
            return new GitRemoteRepo(name, repo);
        }

        private static RepoActivityItem Item(long number, bool pr = false, string? url = null) => new()
        {
            Number = number,
            Title = "Item " + number,
            Author = "someone",
            CreatedAtUtc = T0.UtcDateTime.AddDays(-number),
            IsPullRequest = pr,
            HtmlUrl = url,
        };

        private static RepoIssuesResult Ok(params RepoActivityItem[] items)
            => OkWithETag("\"etag\"", items);

        private static RepoIssuesResult OkWithETag(string etag, params RepoActivityItem[] items)
            => new() { Status = RepoIssuesStatus.Ok, Items = items, ETag = etag };

        private sealed class FakeHost : IRepoActivityHost
        {
            public ConcurrentDictionary<string, Project> Projects { get; } = new();

            public HashSet<string> OpenPanes { get; } = new();

            public ConcurrentDictionary<string, string> Tokens { get; } = new();

            public IReadOnlyList<Project> GetWatchedProjects()
                => Projects.Values.Where(p => p.IsRepoWatchEnabled).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

            public Project? GetProject(string projectId) => Projects.TryGetValue(projectId, out var p) ? p : null;

            public bool HasOpenPane(string projectId)
            {
                lock (OpenPanes)
                {
                    return OpenPanes.Contains(projectId);
                }
            }

            public string? GetGitHubToken(Project project) => Tokens.TryGetValue(project.Id, out var t) ? t : null;
        }

        private sealed record ClientCall(string Repo, string? Token, string? ETag);

        private sealed class FakeClient : IRepoIssuesClient
        {
            private readonly ConcurrentQueue<ClientCall> _calls = new();

            /// <summary>(owner/repo, etag) -> result. Default: Ok with nothing open.</summary>
            public Func<string, string?, RepoIssuesResult> Respond { get; set; } = (_, _) => Ok();

            /// <summary>When set, used instead of <see cref="Respond"/>.</summary>
            public Func<string, string?, Task<RepoIssuesResult>>? RespondAsync { get; set; }

            public IReadOnlyList<ClientCall> Calls => _calls.ToList();

            public Task<RepoIssuesResult> GetOpenIssuesAsync(string owner, string repo, string? token, string? etag, CancellationToken ct = default)
            {
                string fullName = owner + "/" + repo;
                _calls.Enqueue(new ClientCall(fullName, token, etag));
                return RespondAsync != null ? RespondAsync(fullName, etag) : Task.FromResult(Respond(fullName, etag));
            }
        }
    }
}
