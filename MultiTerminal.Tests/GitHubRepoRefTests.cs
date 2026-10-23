#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// <see cref="GitHubRepoRef.TryParse"/> and <see cref="GitRemoteReader.ParseRemoteVerbose"/>
    /// (task df58b35d, GitHub #53): which GitHub repositories a project folder's remotes point at.
    /// All but the last fact are pure parsing; the last runs git once, in an empty temp folder.
    /// </summary>
    public sealed class GitHubRepoRefTests
    {
        // ---- The one github.com page rule (IsPageUrl / IsItemUrl / ItemUrl) ----------------------

        [Theory]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53", true)]
        [InlineData("https://github.com/clarionlive/MULTITERMINAL/pull/56", true)]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues", true)]
        [InlineData("https://github.com/ClarionLive/multiterminal/pulls", true)]
        [InlineData("https://github.com/ClarionLive/multiterminal", false)]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53?x=1", false)]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53#c", false)]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/../../evil", false)]
        [InlineData("https://github.com/ClarionLive/multiterminalX/issues/1", false)]
        [InlineData("https://github.com.evil.com/ClarionLive/multiterminal/issues/1", false)]
        [InlineData("https://user@github.com/ClarionLive/multiterminal/issues/1", false)]
        [InlineData("http://github.com/ClarionLive/multiterminal/issues/1", false)]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/1234567890123456789", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_this_repos_issue_and_pull_pages_are_its_pages(string? url, bool expected)
        {
            Assert.True(GitHubRepoRef.TryParseFullName("ClarionLive/multiterminal", out var repo));
            Assert.Equal(expected, repo.IsPageUrl(url));
        }

        [Theory]
        [InlineData("https://github.com/o/r/issues/7", 7, true)]
        [InlineData("https://github.com/o/r/pull/7", 7, true)]
        [InlineData("https://github.com/o/r/issues/7", 8, false)]
        [InlineData("https://github.com/o/r/issues", 7, false)]
        public void An_item_url_must_carry_that_items_number(string url, long number, bool expected)
        {
            Assert.True(GitHubRepoRef.TryParseFullName("o/r", out var repo));
            Assert.Equal(expected, repo.IsItemUrl(url, number));
        }

        [Fact]
        public void Built_links_pass_the_rule_they_are_checked_against()
        {
            Assert.True(GitHubRepoRef.TryParseFullName("o/r", out var repo));
            Assert.Equal("https://github.com/o/r/issues/3", repo.ItemUrl(3, isPullRequest: false));
            Assert.Equal("https://github.com/o/r/pull/4", repo.ItemUrl(4, isPullRequest: true));
            Assert.True(repo.IsItemUrl(repo.ItemUrl(4, true), 4));
            Assert.True(repo.IsPageUrl(repo.IssuesUrl));
            Assert.True(repo.IsPageUrl(repo.PullsUrl));
            Assert.False(default(GitHubRepoRef).IsPageUrl("https://github.com//issues"));
        }

        [Theory]
        [InlineData("https://github.com/ClarionLive/multiterminal")]
        [InlineData("https://github.com/ClarionLive/multiterminal.git")]
        [InlineData("https://github.com/ClarionLive/multiterminal/")]
        [InlineData("https://github.com/ClarionLive/multiterminal.git/")]
        [InlineData("https://someone@github.com/ClarionLive/multiterminal.git")]
        [InlineData("https://someone:ghp_secret123@github.com/ClarionLive/multiterminal.git")]
        [InlineData("https://GitHub.COM/ClarionLive/multiterminal")]
        [InlineData("HTTPS://github.com/ClarionLive/multiterminal")]
        [InlineData("  https://github.com/ClarionLive/multiterminal.git  ")]
        [InlineData("git@github.com:ClarionLive/multiterminal")]
        [InlineData("git@github.com:ClarionLive/multiterminal.git")]
        [InlineData("git@GITHUB.com:ClarionLive/multiterminal.git")]
        [InlineData("ssh://git@github.com/ClarionLive/multiterminal")]
        [InlineData("ssh://git@github.com/ClarionLive/multiterminal.git")]
        [InlineData("ssh://git@github.com:22/ClarionLive/multiterminal")]
        public void Accepts_github_com_remote_forms(string url)
        {
            Assert.True(GitHubRepoRef.TryParse(url, out var repo));
            Assert.Equal("ClarionLive", repo.Owner);
            Assert.Equal("multiterminal", repo.Name);
            Assert.Equal("ClarionLive/multiterminal", repo.FullName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("http://github.com/ClarionLive/multiterminal")]
        [InlineData("git://github.com/ClarionLive/multiterminal.git")]
        [InlineData("https://gitlab.com/ClarionLive/multiterminal")]
        [InlineData("https://github.com.evil.com/ClarionLive/multiterminal")]
        [InlineData("https://evil.com/github.com/ClarionLive")]
        [InlineData("https://github.evil.com/ClarionLive/multiterminal")]
        [InlineData("https://github.example.org/ClarionLive/multiterminal")]
        [InlineData("https://github.com:443/ClarionLive/multiterminal")]
        [InlineData("https://github.com@evil.com/ClarionLive/multiterminal")]
        [InlineData("git@gitlab.com:ClarionLive/multiterminal.git")]
        [InlineData("git@github.com.evil.com:ClarionLive/multiterminal.git")]
        [InlineData("me@github.com:ClarionLive/multiterminal.git")]
        [InlineData("ssh://git@github.com:2222/ClarionLive/multiterminal")]
        [InlineData("ssh://me@github.com/ClarionLive/multiterminal")]
        [InlineData("ssh://git@gitlab.com/ClarionLive/multiterminal")]
        [InlineData("https://github.com/ClarionLive/multiterminal/tree/main")]
        [InlineData("https://github.com/ClarionLive/multiterminal/issues/53")]
        [InlineData("https://github.com/ClarionLive")]
        [InlineData("https://github.com/")]
        [InlineData("https://github.com//multiterminal")]
        [InlineData("https://github.com/ClarionLive/")]
        [InlineData("https://github.com/ClarionLive/.git")]
        [InlineData("https://github.com/ClarionLive/multiterminal?tab=readme")]
        [InlineData("git@github.com:/ClarionLive/multiterminal.git")]
        [InlineData("C:\\src\\multiterminal")]
        [InlineData("../multiterminal")]
        public void Rejects_other_hosts_schemes_and_shapes(string? url)
        {
            Assert.False(GitHubRepoRef.TryParse(url, out var repo));
            Assert.Equal(default, repo);
        }

        [Theory]
        [InlineData("-owner")]
        [InlineData("owner-")]
        [InlineData("own--er")]
        [InlineData("own_er")]
        [InlineData("own.er")]
        [InlineData("ownér")]
        [InlineData("a234567890123456789012345678901234567890")] // 40 characters
        public void Rejects_owner_names_GitHub_does_not_allow(string owner)
        {
            Assert.False(GitHubRepoRef.TryParse($"https://github.com/{owner}/repo", out _));
        }

        [Theory]
        [InlineData("a")]
        [InlineData("a-b")]
        [InlineData("A1-b2-C3")]
        [InlineData("a23456789012345678901234567890123456789")] // 39 characters
        public void Accepts_owner_names_GitHub_allows(string owner)
        {
            Assert.True(GitHubRepoRef.TryParse($"https://github.com/{owner}/repo", out var repo));
            Assert.Equal(owner, repo.Owner);
        }

        [Theory]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("re po")]
        [InlineData("re+po")]
        [InlineData("répo")]
        public void Rejects_repository_names_GitHub_does_not_allow(string name)
        {
            Assert.False(GitHubRepoRef.TryParse($"https://github.com/owner/{name}", out _));
        }

        [Fact]
        public void Repository_name_length_limit_is_100()
        {
            string hundred = new string('r', 100);
            Assert.True(GitHubRepoRef.TryParse($"https://github.com/owner/{hundred}", out var repo));
            Assert.Equal(hundred, repo.Name);
            Assert.False(GitHubRepoRef.TryParse($"https://github.com/owner/{hundred}r", out _));
        }

        [Theory]
        [InlineData("my_repo.js")]
        [InlineData(".github")]
        [InlineData("a.b-c_d")]
        public void Accepts_repository_names_GitHub_allows(string name)
        {
            Assert.True(GitHubRepoRef.TryParse($"git@github.com:owner/{name}.git", out var repo));
            Assert.Equal(name, repo.Name);
        }

        [Fact]
        public void Equality_ignores_case_on_owner_and_name()
        {
            Assert.True(GitHubRepoRef.TryParse("https://github.com/ClarionLive/MultiTerminal", out var a));
            Assert.True(GitHubRepoRef.TryParse("git@github.com:clarionlive/multiterminal.git", out var b));
            Assert.True(GitHubRepoRef.TryParse("git@github.com:clarionlive/other.git", out var c));
            Assert.True(GitHubRepoRef.TryParse("git@github.com:someoneelse/multiterminal.git", out var d));

            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, c);
            Assert.NotEqual(a, d);
            Assert.True(a != c);
            Assert.Single(new HashSet<GitHubRepoRef> { a, b });
        }

        [Fact]
        public void Credentials_in_an_https_url_are_not_kept()
        {
            Assert.True(GitHubRepoRef.TryParse("https://john:ghp_SuperSecretToken@github.com/ClarionLive/multiterminal.git", out var repo));

            Assert.Equal("ClarionLive/multiterminal", repo.FullName);
            Assert.Equal("ClarionLive/multiterminal", repo.ToString());
            Assert.DoesNotContain("ghp_SuperSecretToken", repo.Owner + repo.Name + repo.FullName + repo.ToString());
            Assert.DoesNotContain("john", repo.Owner + repo.Name + repo.FullName + repo.ToString());
        }

        [Fact]
        public void ParseRemoteVerbose_keeps_every_github_fetch_remote_deduplicated_in_git_order()
        {
            // A fork: origin is the fork, upstream the original. "mirror" names the original
            // again in another case and form; "corp" is not GitHub. upstream's push URL is
            // disabled (a common safety setup) and "pushonly" differs only on its push line.
            const string output =
                "corp\thttps://gitlab.example.com/team/multiterminal.git (fetch)\r\n" +
                "corp\thttps://gitlab.example.com/team/multiterminal.git (push)\r\n" +
                "mirror\tgit@github.com:clarionlive/MULTITERMINAL.git (fetch)\r\n" +
                "mirror\tgit@github.com:clarionlive/MULTITERMINAL.git (push)\r\n" +
                "origin\thttps://github.com/msarson/multiterminal.git (fetch)\r\n" +
                "origin\thttps://github.com/msarson/multiterminal.git (push)\r\n" +
                "pushonly\thttps://gitlab.example.com/x/y.git (fetch)\r\n" +
                "pushonly\thttps://github.com/pushtarget/elsewhere.git (push)\r\n" +
                "upstream\thttps://github.com/ClarionLive/multiterminal.git (fetch)\r\n" +
                "upstream\tno_push (push)\r\n";

            var remotes = GitRemoteReader.ParseRemoteVerbose(output);

            Assert.Equal(
                new[] { "mirror:clarionlive/MULTITERMINAL", "origin:msarson/multiterminal" },
                remotes.Select(r => $"{r.RemoteName}:{r.Repo.FullName}").ToArray());
        }

        [Fact]
        public void ParseRemoteVerbose_handles_lf_only_and_ignores_push_lines()
        {
            const string output =
                "origin\tgit@github.com:msarson/multiterminal.git (fetch)\n" +
                "origin\tgit@github.com:pushfork/other.git (push)\n" +
                "upstream\thttps://github.com/ClarionLive/multiterminal (fetch)\n" +
                "upstream\thttps://github.com/ClarionLive/multiterminal (push)\n";

            var remotes = GitRemoteReader.ParseRemoteVerbose(output);

            Assert.Equal(2, remotes.Count);
            Assert.Equal("origin", remotes[0].RemoteName);
            Assert.Equal("msarson/multiterminal", remotes[0].Repo.FullName);
            Assert.Equal("upstream", remotes[1].RemoteName);
            Assert.Equal("ClarionLive/multiterminal", remotes[1].Repo.FullName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("\n")]
        [InlineData("fatal: not a git repository (or any of the parent directories): .git\n")]
        public void ParseRemoteVerbose_returns_empty_for_no_remotes(string? output)
        {
            Assert.Empty(GitRemoteReader.ParseRemoteVerbose(output));
        }

        [Fact]
        public async Task GetGitHubRemotesAsync_returns_empty_for_a_missing_folder_or_a_non_repo()
        {
            string missing = Path.Combine(Path.GetTempPath(), "mt-no-such-folder-" + Guid.NewGuid().ToString("N"));
            Assert.Empty(await GitRemoteReader.GetGitHubRemotesAsync(missing));
            Assert.Empty(await GitRemoteReader.GetGitHubRemotesAsync(string.Empty));

            // A fresh folder outside any repository: git exits non-zero ("not a git repository").
            string empty = Directory.CreateTempSubdirectory("mt-remotes-").FullName;
            try
            {
                Assert.Empty(await GitRemoteReader.GetGitHubRemotesAsync(empty));
            }
            finally
            {
                Directory.Delete(empty, recursive: true);
            }
        }
    }
}
