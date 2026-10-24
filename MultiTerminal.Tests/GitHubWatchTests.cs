using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 92eeeb47: MT watches a project's GitHub repo for new issues and PRs. The rules that keep it
    /// safe to turn on: the first check adds nothing (no backlog dump), an item is reported once,
    /// everything from GitHub is untrusted, and only github.com repos are ever called.
    /// </summary>
    public sealed class GitHubWatchTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly ProjectDatabase _db;

        public GitHubWatchTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"mt_ghwatch_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _dbPath);
            _db = new ProjectDatabase();
        }

        public void Dispose()
        {
            _db.Dispose();
            SQLiteConnection.ClearAllPools();
            foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
        }

        // ── Repository URL ───────────────────────────────────────────────────────

        [Theory]
        [InlineData("https://github.com/ClarionLive/multiterminal", "ClarionLive/multiterminal")]
        [InlineData("https://github.com/ClarionLive/multiterminal.git", "ClarionLive/multiterminal")]
        [InlineData("https://www.github.com/ClarionLive/multiterminal/", "ClarionLive/multiterminal")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/52", "ClarionLive/multiterminal")]
        [InlineData("git@github.com:msarson/clarion-lsp.git", "msarson/clarion-lsp")]
        [InlineData("ssh://git@github.com/msarson/clarion-lsp.git", "msarson/clarion-lsp")]
        [InlineData("  ClarionLive/multiterminal  ", "ClarionLive/multiterminal")]
        public void A_github_repository_url_in_any_common_form_names_owner_and_repo(string url, string expected)
        {
            Assert.Equal(expected, GitHubRepoRef.TryParse(url)?.FullName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("https://bitbucket.org/team/repo")]
        [InlineData("https://github.example.com/team/repo")]   // Enterprise: the watcher calls api.github.com only
        [InlineData("https://github.com/onlyowner")]
        [InlineData("https://github.com/own%2Fer/repo")]         // an encoded slash must not reach the API path
        [InlineData("https://github.com/-bad/repo")]
        [InlineData("https://github.com/owner/..")]
        [InlineData(@"H:\Dev\repo")]
        [InlineData("file:///H:/Dev/repo")]
        public void Anything_that_is_not_a_github_com_repository_is_not_watchable(string url)
        {
            Assert.Null(GitHubRepoRef.TryParse(url));
        }

        // ── Response parsing ─────────────────────────────────────────────────────

        private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public void Issues_and_PRs_are_read_from_one_list_and_malformed_entries_are_skipped()
        {
            var result = GitHubIssueFeed.Parse(Json(@"[
                {""number"": 12, ""title"": ""Crash on start"", ""html_url"": ""https://github.com/o/r/issues/12"", ""user"": {""login"": ""alice""}, ""created_at"": ""2026-10-24T10:00:00Z""},
                {""number"": 13, ""title"": ""Fix it"", ""html_url"": ""https://github.com/o/r/pull/13"", ""user"": {""login"": ""bob""}, ""pull_request"": {""url"": ""x""}},
                {""number"": ""14"", ""title"": ""number is a string""},
                {""title"": ""no number""},
                42
            ]"));

            Assert.True(result.Ok);
            Assert.Equal(new[] { 12, 13 }, result.Items.Select(i => i.Number));
            Assert.False(result.Items[0].IsPullRequest);
            Assert.True(result.Items[1].IsPullRequest);
            Assert.Equal("alice", result.Items[0].Author);
        }

        [Fact]
        public void Text_from_GitHub_is_stripped_of_control_and_bidi_characters_and_capped()
        {
            string hostile = "Fix\u202Egnp.exe\u200B\r\nnow" + new string('x', 500);
            string clean = GitHubIssueFeed.Sanitize(hostile, GitHubIssueFeed.MaxTitleLength);

            Assert.DoesNotContain('\u202E', clean);
            Assert.DoesNotContain('\u200B', clean);
            Assert.DoesNotContain('\n', clean);
            Assert.True(clean.Length <= GitHubIssueFeed.MaxTitleLength);
            Assert.StartsWith("Fix gnp.exe now", clean);
        }

        [Theory]
        [InlineData("https://github.com/o/r/issues/1", true)]
        [InlineData("http://github.com/o/r/issues/1", false)]
        [InlineData("https://evil.example/o/r/issues/1", false)]
        [InlineData("javascript:alert(1)", false)]
        public void Only_https_github_com_links_are_kept(string url, bool kept)
        {
            Assert.Equal(kept, GitHubIssueFeed.SafeGitHubUrl(url) != null);
        }

        // ── What is new, and when a check is due ────────────────────────────────

        private static GitHubItem Item(int n, bool pr = false) => new GitHubItem { Number = n, IsPullRequest = pr, Title = "t" + n };

        [Fact]
        public void The_first_check_only_records_a_baseline_so_the_backlog_is_not_added()
        {
            var plan = GitHubWatchService.PlanCheck(new[] { Item(40), Item(41), Item(39) }, baseline: null);

            Assert.Empty(plan.NewItems);
            Assert.Equal(41, plan.NewBaseline);
        }

        [Fact]
        public void Later_checks_report_only_numbers_above_the_baseline_oldest_first_and_the_baseline_never_drops()
        {
            var plan = GitHubWatchService.PlanCheck(new[] { Item(44, pr: true), Item(41), Item(43), Item(42) }, baseline: 42);
            Assert.Equal(new[] { 43, 44 }, plan.NewItems.Select(i => i.Number));
            Assert.Equal(44, plan.NewBaseline);

            var older = GitHubWatchService.PlanCheck(new[] { Item(10) }, baseline: 44);
            Assert.Empty(older.NewItems);
            Assert.Equal(44, older.NewBaseline);
        }

        [Theory]
        [InlineData(GitHubCheckInterval.Off, null, false)]
        [InlineData(GitHubCheckInterval.Hour1, null, true)]
        [InlineData(GitHubCheckInterval.Hour1, 30, false)]
        [InlineData(GitHubCheckInterval.Hour1, 61, true)]
        [InlineData(GitHubCheckInterval.Daily, 60 * 23, false)]
        [InlineData(GitHubCheckInterval.Daily, 60 * 24, true)]
        public void A_timed_check_is_due_once_its_period_has_passed_since_the_last_check(GitHubCheckInterval interval, int? minutesAgo, bool due)
        {
            var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            DateTime? last = minutesAgo.HasValue ? now.AddMinutes(-minutesAgo.Value) : (DateTime?)null;
            Assert.Equal(due, GitHubWatchService.IsDue(interval, last, now));
        }

        // ── What the form says ───────────────────────────────────────────────────

        [Theory]
        [InlineData("https://bitbucket.org/o/r", "github", true, "not a github.com repository")]
        [InlineData("https://github.com/o/r", null, false, "link a GitHub source control account")]
        [InlineData("https://github.com/o/r", "bitbucket", true, "not a GitHub account")]
        [InlineData("https://github.com/o/r", "github", false, "no token saved")]
        [InlineData("https://github.com/o/r", "github", true, "Ready to watch o/r")]
        public void The_form_says_why_a_project_cannot_be_watched(string repoUrl, string provider, bool hasToken, string expected)
        {
            var p = Project.Create("P", Path.GetTempPath());
            p.GitRepoUrl = repoUrl;
            Assert.Contains(expected, GitHubWatchService.DescribeStatus(p, provider, hasToken, null, null));
        }

        [Fact]
        public void The_form_shows_the_last_checks_failure()
        {
            var p = Project.Create("P", Path.GetTempPath());
            p.GitRepoUrl = "https://github.com/o/r";
            string status = GitHubWatchService.DescribeStatus(p, "github", true, DateTime.UtcNow, "GitHub returned 500.");
            Assert.Contains("failed: GitHub returned 500.", status);
        }

        // ── End to end, against a stub GitHub and a real database ──────────────

        private sealed class StubGitHub : HttpMessageHandler
        {
            public string Body { get; set; } = "[]";
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
            }
        }

        private static string Page(params int[] numbers) =>
            "[" + string.Join(",", numbers.Select(n =>
                $"{{\"number\":{n},\"title\":\"Item {n}\",\"html_url\":\"https://github.com/o/r/issues/{n}\",\"user\":{{\"login\":\"u\"}}}}")) + "]";

        private (GitHubWatchService Service, StubGitHub GitHub, List<string> Cards, List<string> Pushes, Project Project) Build(
            GitHubWatchMode mode, string token = "tok", string repoUrl = "https://github.com/o/r")
        {
            var project = Project.Create("Watched", Path.GetTempPath());
            project.GitRepoUrl = repoUrl;
            project.SourceControlAccountId = "acct";
            project.GitHubWatchMode = mode;
            _db.SaveRichProject(project);

            var github = new StubGitHub();
            var cards = new List<string>();
            var pushes = new List<string>();
            var service = new GitHubWatchService(
                _db,
                () => new[] { project },
                accountId => accountId == "acct" ? token : null,
                new GitHubIssueFeed(github),
                (title, description, projectId) => { cards.Add(title); return "task-" + cards.Count; },
                (title, body) => pushes.Add(title + "|" + body),
                _ => { });
            return (service, github, cards, pushes, project);
        }

        [Fact]
        public async Task Board_mode_adds_each_new_item_once_after_the_baseline_check()
        {
            var (service, github, cards, pushes, project) = Build(GitHubWatchMode.Board);

            github.Body = Page(41, 40);
            Assert.Equal(0, await service.CheckProjectAsync(project, "test"));
            Assert.Empty(cards);
            Assert.Empty(pushes);

            github.Body = Page(43, 42, 41);
            Assert.Equal(2, await service.CheckProjectAsync(project, "test"));
            Assert.Equal(new[] { "[GitHub issue #42] Item 42", "[GitHub issue #43] Item 43" }, cards);
            Assert.Single(pushes);

            // The same page again: nothing new, nothing added twice.
            Assert.Equal(0, await service.CheckProjectAsync(project, "test"));
            Assert.Equal(2, cards.Count);
            Assert.Single(pushes);

            var request = github.Requests.Last();
            Assert.Equal("api.github.com", request.RequestUri.Host);
            Assert.Equal("/repos/o/r/issues", request.RequestUri.AbsolutePath);
            Assert.Equal("tok", request.Headers.Authorization?.Parameter);
        }

        [Fact]
        public async Task An_item_claimed_once_is_never_reported_again_even_if_the_baseline_were_lost()
        {
            var (service, github, cards, _, project) = Build(GitHubWatchMode.Board);
            github.Body = Page(10);
            await service.CheckProjectAsync(project, "baseline");
            github.Body = Page(11, 10);
            await service.CheckProjectAsync(project, "first");
            Assert.Single(cards);

            // The claim table, not the baseline, is the last word on "new".
            Assert.False(_db.TryClaimGitHubItem(project.Id, "o/r", 11, isPullRequest: false));
        }

        [Fact]
        public async Task Notify_mode_pushes_but_adds_no_cards()
        {
            var (service, github, cards, pushes, project) = Build(GitHubWatchMode.Notify);
            github.Body = Page(5);
            await service.CheckProjectAsync(project, "baseline");
            github.Body = Page(6, 5);
            await service.CheckProjectAsync(project, "check");

            Assert.Empty(cards);
            Assert.Single(pushes);
            Assert.Contains("Issue #6", pushes[0]);
        }

        [Fact]
        public async Task No_GitHub_token_means_no_call_and_no_cards()
        {
            var (service, github, cards, pushes, project) = Build(GitHubWatchMode.Board, token: null);

            Assert.Equal(0, await service.CheckProjectAsync(project, "check"));
            Assert.Empty(github.Requests);
            Assert.Empty(cards);
            Assert.Empty(pushes);
        }

        [Fact]
        public async Task A_repository_url_that_is_not_github_com_is_never_called()
        {
            var (service, github, _, _, project) = Build(GitHubWatchMode.Board, repoUrl: "https://bitbucket.org/o/r");

            await service.CheckProjectAsync(project, "check");
            Assert.Empty(github.Requests);
        }

        [Fact]
        public async Task A_GitHub_error_adds_nothing_and_keeps_the_baseline()
        {
            var (service, github, cards, pushes, project) = Build(GitHubWatchMode.Board);
            github.Body = Page(7);
            await service.CheckProjectAsync(project, "baseline");

            github.Status = HttpStatusCode.Unauthorized;
            github.Body = Page(9, 8, 7);
            Assert.Equal(0, await service.CheckProjectAsync(project, "check"));
            Assert.Empty(cards);
            Assert.Empty(pushes);
            Assert.Equal(7, _db.GetGitHubWatchState(project.Id, "o/r").Baseline);
        }
    }
}
