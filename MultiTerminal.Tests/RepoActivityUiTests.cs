#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiTerminal.Controls;
using MultiTerminal.Models;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The status bar side of task df58b35d (GitHub #53): the badge built from a
    /// <see cref="RepoActivitySummary"/>, the list a click on it opens, the one gate a link passes before
    /// the shell opens it, and the string contract between <see cref="TerminalStatusBarRenderer"/> and
    /// statusbar.html.
    ///
    /// <para>WHAT IS NOT COVERED: the WebView2 page and the native menu are not instantiated here. The
    /// contract facts are source-level assertions over comment-stripped statusbar.html (whole-line
    /// <c>//</c> and block comments removed, so the JS comment that lists the payload's fields cannot
    /// satisfy them), scoped to the function that reads or posts the message. That a click on the
    /// badge opens the list needs the running app.</para>
    /// </summary>
    public sealed class RepoActivityUiTests
    {
        private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        // ── RepoActivityBadge.From ───────────────────────────────────────────

        [Fact]
        public void No_summary_or_not_watching_hides_the_badge()
        {
            Assert.Null(RepoActivityBadge.From(null, Now));
            Assert.Null(RepoActivityBadge.From(new RepoActivitySummary { Watching = false, State = RepoActivityState.Disabled, Reason = "off" }, Now));
        }

        [Fact]
        public void An_ok_summary_carries_its_counts_and_says_them_in_the_tooltip()
        {
            var badge = RepoActivityBadge.From(Summary(RepoActivityState.Ok, issues: 3, prs: 1, newCount: 2, lastChecked: Now.AddMinutes(-5)), Now)!;

            Assert.Equal(3, badge.Issues);
            Assert.Equal(1, badge.PullRequests);
            Assert.Equal(2, badge.NewCount);
            Assert.False(badge.Truncated);
            Assert.False(badge.Dim);
            Assert.Equal("Ok", badge.State);
            Assert.Equal("3 open issues, 1 open PR (2 new since you last looked) — last checked 5 min ago", badge.Tooltip);
        }

        [Fact]
        public void No_new_items_says_nothing_about_new()
        {
            var badge = RepoActivityBadge.From(Summary(RepoActivityState.Ok, issues: 1, prs: 0, newCount: 0, lastChecked: Now.AddHours(-3)), Now)!;
            Assert.Equal("1 open issue, 0 open PRs — last checked 3 h ago", badge.Tooltip);
        }

        [Fact]
        public void A_full_page_is_flagged_and_the_tooltip_says_at_least()
        {
            var badge = RepoActivityBadge.From(Summary(RepoActivityState.Ok, issues: 60, prs: 40, truncated: true, lastChecked: Now), Now)!;
            Assert.True(badge.Truncated);
            Assert.StartsWith("At least 60 open issues, 40 open PRs", badge.Tooltip, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(RepoActivityState.Ok, false)]
        [InlineData(RepoActivityState.Checking, false)]
        [InlineData(RepoActivityState.NeverChecked, true)]
        [InlineData(RepoActivityState.NoRepos, true)]
        [InlineData(RepoActivityState.NoAccess, true)]
        [InlineData(RepoActivityState.RateLimited, true)]
        [InlineData(RepoActivityState.Error, true)]
        public void Dim_for_every_state_but_ok_and_checking(RepoActivityState state, bool dim)
        {
            var badge = RepoActivityBadge.From(Summary(state, issues: 1, prs: 1, lastChecked: Now, reason: "why"), Now)!;
            Assert.Equal(dim, badge.Dim);
        }

        [Fact]
        public void A_failure_keeps_the_last_counts_and_adds_the_reason()
        {
            var summary = Summary(RepoActivityState.NoAccess, issues: 2, prs: 1, newCount: 1, lastChecked: Now.AddMinutes(-10),
                reason: "ClarionLive/x: GitHub found no repository ClarionLive/x (404).");
            var badge = RepoActivityBadge.From(summary, Now)!;

            Assert.Equal(2, badge.Issues);
            Assert.Contains("2 open issues, 1 open PR (1 new since you last looked)", badge.Tooltip, StringComparison.Ordinal);
            Assert.EndsWith("\nClarionLive/x: GitHub found no repository ClarionLive/x (404).", badge.Tooltip, StringComparison.Ordinal);
        }

        [Fact]
        public void Never_checked_and_no_repos_have_no_counts_only_the_reason()
        {
            var never = RepoActivityBadge.From(Summary(RepoActivityState.NeverChecked, reason: "Not checked yet."), Now)!;
            Assert.Null(never.Issues);
            Assert.Null(never.PullRequests);
            Assert.Equal("Not checked yet.", never.Tooltip);

            var noRepos = RepoActivityBadge.From(Summary(RepoActivityState.NoRepos, reason: null), Now)!;
            Assert.Null(noRepos.Issues);
            Assert.Equal("No GitHub repository found for this project.", noRepos.Tooltip);
        }

        [Fact]
        public void The_payload_carries_no_link_and_no_item()
        {
            var summary = Summary(RepoActivityState.Error, issues: 1, prs: 0, lastChecked: Now,
                reason: "owner/repo: Network error contacting GitHub: https://api.github.com/repos/owner/repo/issues failed",
                withItem: true);
            string json = RepoActivityBadge.ToJson(RepoActivityBadge.From(summary, Now));

            Assert.DoesNotContain("://", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Secret item title", json, StringComparison.Ordinal);
            Assert.Contains("Network error contacting GitHub", json, StringComparison.Ordinal);
            Assert.Equal("null", RepoActivityBadge.ToJson(null));
        }

        // ── IsAllowedRepoUrl ─────────────────────────────────────────────────

        [Theory]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53")]
        [InlineData("https://github.com/ClarionLive/multiterminal/pull/7")]
        [InlineData("https://github.com/clarionlive/MultiTerminal/issues/53")]
        [InlineData("https://GitHub.com/ClarionLive/multiterminal/pull/7")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues")]
        [InlineData("https://github.com/ClarionLive/multiterminal/pulls")]
        public void Accepts_the_repository_s_own_item_and_list_pages(string url)
        {
            Assert.True(RepoActivityMenu.IsAllowedRepoUrl(url, "ClarionLive/multiterminal"));
        }

        [Theory]
        [InlineData("https://github.com/ClarionLive/other/issues/53")]
        [InlineData("https://github.com/ClarionLive/multiterminal-evil/issues/53")]
        [InlineData("https://gitlab.com/ClarionLive/multiterminal/issues/53")]
        [InlineData("http://github.com/ClarionLive/multiterminal/issues/53")]
        [InlineData("https://github.com.evil.com/ClarionLive/multiterminal/issues/53")]
        [InlineData("https://user@github.com/ClarionLive/multiterminal/issues/53")]
        [InlineData("https://github.com:8443/ClarionLive/multiterminal/issues/53")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53?x=1")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53#top")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53/../../../other/issues/1")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/%35%33")]
        [InlineData("https://github.com/ClarionLive/multiterminal/settings")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/")]
        [InlineData("https://github.com/ClarionLive/multiterminal")]
        [InlineData("javascript:alert(1)//https://github.com/ClarionLive/multiterminal/issues/1")]
        [InlineData("")]
        [InlineData(null)]
        public void Rejects_anything_else(string? url)
        {
            Assert.False(RepoActivityMenu.IsAllowedRepoUrl(url, "ClarionLive/multiterminal"));
        }

        [Fact]
        public void Rejects_when_the_repository_name_is_not_a_valid_owner_repo()
        {
            Assert.False(RepoActivityMenu.IsAllowedRepoUrl("https://github.com/a/b/issues/1", "a/b/c"));
            Assert.False(RepoActivityMenu.IsAllowedRepoUrl("https://github.com/a/b/issues/1", null));
        }

        // ── The list ─────────────────────────────────────────────────────────

        [Fact]
        public void The_list_shows_each_repository_then_its_items_new_first_then_the_actions()
        {
            var summary = new RepoActivitySummary
            {
                ProjectId = "p1",
                Watching = true,
                State = RepoActivityState.Ok,
                OpenIssues = 1,
                OpenPullRequests = 1,
                NewCount = 1,
                LastCheckedUtc = Now,
                Repos = new[]
                {
                    Repo("ClarionLive/multiterminal",
                        Entry(53, "Show new issues", isNew: true, pr: false, ageDays: 2),
                        Entry(50, "Old PR", isNew: false, pr: true, draft: true, ageDays: 9)),
                },
            };

            var entries = RepoActivityMenu.BuildEntries(summary, Now);
            var texts = entries.Select(e => e.IsSeparator ? "---" : e.Text).ToList();

            Assert.Equal(
                new[]
                {
                    "ClarionLive/multiterminal",
                    "● #53  Show new issues — alice, 2d",
                    "    #50  [Draft PR]  Old PR — alice, 9d",
                    "---",
                    "Mark all seen",
                    "Check now",
                    "Open issues on GitHub",
                    "Open pull requests on GitHub",
                },
                texts);

            Assert.True(entries[0].IsHeader);
            Assert.False(entries[0].Enabled);
            Assert.True(entries[1].IsNew);
            Assert.Equal(RepoActivityMenuAction.OpenUrl, entries[1].Action);
            Assert.Equal("https://github.com/ClarionLive/multiterminal/issues/53", entries[1].Url);
            Assert.True(entries[1].Enabled);
            Assert.True(entries[4].Enabled);
            Assert.Equal("https://github.com/ClarionLive/multiterminal/pulls", entries[7].Url);
            Assert.All(entries.Where(e => e.Action == RepoActivityMenuAction.OpenUrl), e => Assert.Equal("ClarionLive/multiterminal", e.Repo));
        }

        [Fact]
        public void An_item_whose_link_fails_the_gate_is_listed_but_cannot_be_clicked()
        {
            var summary = new RepoActivitySummary
            {
                Watching = true,
                State = RepoActivityState.Ok,
                Repos = new[] { Repo("a/b", new RepoActivityEntry(Item(1, "x", false, false, 0), false, "https://evil.example/a/b/issues/1")) },
            };

            var item = RepoActivityMenu.BuildEntries(summary, Now).Single(e => e.Text.Contains("#1", StringComparison.Ordinal));
            Assert.False(item.Enabled);
        }

        [Fact]
        public void Long_titles_are_cut_and_long_lists_end_with_more_on_github()
        {
            string longTitle = new string('t', 200);
            var many = Enumerable.Range(1, RepoActivityMenu.MaxItemsPerRepo + 5)
                .Select(n => Entry(n, longTitle, isNew: false, pr: false, ageDays: 0, repo: "a/b"))
                .ToArray();
            var summary = new RepoActivitySummary { Watching = true, State = RepoActivityState.Ok, Repos = new[] { Repo("a/b", many) } };

            var entries = RepoActivityMenu.BuildEntries(summary, Now);
            var items = entries.Where(e => e.Text.Contains(" #", StringComparison.Ordinal)).ToList();

            Assert.Equal(RepoActivityMenu.MaxItemsPerRepo, items.Count);
            Assert.All(items, e => Assert.True(e.Enabled));
            Assert.All(items, e => Assert.Contains(new string('t', RepoActivityMenu.MaxTitleLength - 1) + "… —", e.Text, StringComparison.Ordinal));
            var more = Assert.Single(entries, e => e.Text == "More on GitHub…");
            Assert.Equal("https://github.com/a/b/issues", more.Url);
            Assert.True(more.Enabled);
        }

        [Fact]
        public void A_failing_project_lists_the_reason_disabled()
        {
            var summary = new RepoActivitySummary
            {
                Watching = true,
                State = RepoActivityState.RateLimited,
                Reason = "a/b: rate limited",
                Repos = new[] { new RepoActivityRepo { FullName = "a/b", State = RepoActivityState.RateLimited, Reason = "rate limited" } },
            };

            var entries = RepoActivityMenu.BuildEntries(summary, Now);

            Assert.Contains(entries, e => e.Text == "rate limited" && !e.Enabled);
            Assert.Contains(entries, e => e.Text == "a/b: rate limited" && !e.Enabled);
            Assert.DoesNotContain(entries, e => e.Text == "No open issues or pull requests");
        }

        [Fact]
        public void No_repositories_offers_the_reason_check_now_and_project_settings()
        {
            var summary = new RepoActivitySummary { Watching = true, State = RepoActivityState.NoRepos, Reason = "No GitHub repository to watch." };

            var entries = RepoActivityMenu.BuildEntries(summary, Now);

            Assert.Equal("No GitHub repository to watch.", entries[0].Text);
            Assert.False(entries[0].Enabled);
            Assert.Contains(entries, e => e.Action == RepoActivityMenuAction.CheckNow && e.Enabled);
            Assert.Contains(entries, e => e.Action == RepoActivityMenuAction.ProjectSettings && e.Enabled);
            Assert.DoesNotContain(entries, e => e.Action == RepoActivityMenuAction.OpenUrl);
        }

        [Fact]
        public void Several_repositories_name_their_list_links()
        {
            var summary = new RepoActivitySummary
            {
                Watching = true,
                State = RepoActivityState.Ok,
                Repos = new[] { Repo("a/one"), Repo("b/two") },
            };

            var links = RepoActivityMenu.BuildEntries(summary, Now).Where(e => e.Text.StartsWith("Open ", StringComparison.Ordinal)).ToList();

            Assert.Equal(4, links.Count);
            Assert.Contains(links, e => e.Text == "Open issues on GitHub (b/two)" && e.Url == "https://github.com/b/two/issues" && e.Repo == "b/two");
        }

        [Fact]
        public void Mark_all_seen_is_offered_only_when_something_is_new_and_check_now_not_while_checking()
        {
            var calm = RepoActivityMenu.BuildEntries(new RepoActivitySummary { Watching = true, State = RepoActivityState.Checking, Repos = new[] { Repo("a/b") } }, Now);
            Assert.False(calm.Single(e => e.Action == RepoActivityMenuAction.MarkAllSeen).Enabled);
            Assert.False(calm.Single(e => e.Action == RepoActivityMenuAction.CheckNow).Enabled);
        }

        // ── The renderer <-> statusbar.html string contract ──────────────────

        [Fact]
        public void Statusbar_html_reads_the_exact_prefix_the_renderer_posts()
        {
            string listener = MessageListenerBody();
            string prefix = TerminalStatusBarRenderer.RepoActivityMessagePrefix;

            Assert.Contains($"data.startsWith('{prefix}')", listener, StringComparison.Ordinal);
            Assert.Contains($"data.substring('{prefix}'.length)", listener, StringComparison.Ordinal);
            Assert.Contains("setRepoActivity(", listener, StringComparison.Ordinal);
        }

        [Fact]
        public void Statusbar_html_posts_the_exact_click_type_the_renderer_handles()
        {
            string html = ReadStripped("Controls", "TerminalStatusBar", "statusbar.html");
            string body = BalancedBodyAfter(html, "function onRepoActivityClick(", "onRepoActivityClick()");

            Assert.Contains($"postMessage('{TerminalStatusBarRenderer.RepoActivityClickType}'", body, StringComparison.Ordinal);
            Assert.Contains("onclick=\"onRepoActivityClick()\"", html, StringComparison.Ordinal);
        }

        /// <summary>
        /// The Run-1 CRITICAL shape from 60665c6c (PascalCase sent, camelCase read): every JSON name the
        /// badge serializes must be one setRepoActivity reads, so renaming either side goes red.
        /// </summary>
        [Fact]
        public void Statusbar_html_reads_every_field_the_badge_serializes()
        {
            var badge = new RepoActivityBadge { Issues = 1, PullRequests = 1, NewCount = 1, Truncated = true, State = "Ok", Tooltip = "t", Dim = true };
            using var doc = JsonDocument.Parse(RepoActivityBadge.ToJson(badge));
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(7, names.Count);

            string body = BalancedBodyAfter(ReadStripped("Controls", "TerminalStatusBar", "statusbar.html"), "function setRepoActivity(", "setRepoActivity()");
            Assert.True(body.Length > 500, "setRepoActivity() body was not extracted.");

            var missing = names.Where(n => !Regex.IsMatch(body, @"\bbadge\." + Regex.Escape(n) + @"\b")).ToList();
            Assert.True(missing.Count == 0, "statusbar.html's setRepoActivity() never reads: " + string.Join(", ", missing));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static RepoActivitySummary Summary(
            RepoActivityState state,
            int issues = 0,
            int prs = 0,
            int newCount = 0,
            bool truncated = false,
            DateTime? lastChecked = null,
            string? reason = null,
            bool withItem = false)
            => new()
            {
                ProjectId = "p1",
                Watching = true,
                State = state,
                Reason = reason,
                OpenIssues = issues,
                OpenPullRequests = prs,
                NewCount = newCount,
                Truncated = truncated,
                LastCheckedUtc = lastChecked,
                Repos = withItem
                    ? new[] { Repo("owner/repo", new RepoActivityEntry(Item(1, "Secret item title", false, false, 0), true, "https://github.com/owner/repo/issues/1")) }
                    : Array.Empty<RepoActivityRepo>(),
            };

        private static RepoActivityRepo Repo(string fullName, params RepoActivityEntry[] items)
            => new() { FullName = fullName, State = RepoActivityState.Ok, LastCheckedUtc = Now, Items = items };

        private static RepoActivityEntry Entry(long n, string title, bool isNew, bool pr, int ageDays, bool draft = false, string repo = "ClarionLive/multiterminal")
            => new(Item(n, title, pr, draft, ageDays), isNew, $"https://github.com/{repo}/{(pr ? "pull" : "issues")}/{n}");

        private static RepoActivityItem Item(long n, string title, bool pr, bool draft, int ageDays)
            => new() { Number = n, Title = title, Author = "alice", CreatedAtUtc = Now.AddDays(-ageDays), IsPullRequest = pr, IsDraft = draft };

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string ReadStripped(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            string noBlocks = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join("\n", noBlocks.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string MessageListenerBody()
        {
            string body = BalancedBodyAfter(
                ReadStripped("Controls", "TerminalStatusBar", "statusbar.html"),
                "addEventListener('message'",
                "statusbar.html's message listener");
            Assert.True(body.Length > 1000, "The message listener body was not extracted.");
            return body;
        }

        private static string BalancedBodyAfter(string src, string anchor, string what)
        {
            int start = src.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Could not find {what} (anchor: '{anchor}').");
            int open = src.IndexOf('{', start);
            Assert.True(open >= 0, $"Found {what} but no opening brace followed it.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(open, i - open + 1);
            }

            Assert.Fail($"Unbalanced braces while reading {what}.");
            return string.Empty;
        }
    }
}
