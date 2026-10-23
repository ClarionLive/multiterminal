using System;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The value rules for a project's repository watch settings (ticket df58b35d, GitHub #53): the allowed
    /// check intervals and the owner/repo list. Pure; the storage and the settings save are covered in
    /// ProjectSettingsServiceTests.
    /// </summary>
    public sealed class RepoWatchSettingsTests
    {
        [Theory]
        [InlineData("0", 0)]
        [InlineData("5", 5)]
        [InlineData("15", 15)]
        [InlineData(" 30 ", 30)]
        [InlineData("60", 60)]
        public void An_allowed_interval_parses(string input, int expected)
        {
            Assert.True(RepoWatchSettings.TryParseInterval(input, out int? minutes, out string error), error);
            Assert.Equal(expected, minutes);
            Assert.Null(error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_blank_interval_is_null_which_reads_as_the_default(string input)
        {
            Assert.True(RepoWatchSettings.TryParseInterval(input, out int? minutes, out _));
            Assert.Null(minutes);
            Assert.Equal(15, RepoWatchSettings.DefaultIntervalMinutes);
        }

        /// <summary>
        /// Falsified: dropping the AllowedIntervals membership check turned the "7" and "120" cases red; the
        /// others are refused by the parse itself (NumberStyles.None), so they pin the parse, not the list.
        /// </summary>
        [Theory]
        [InlineData("7")]
        [InlineData("120")]
        [InlineData("-5")]
        [InlineData("+5")]
        [InlineData("15 minutes")]
        [InlineData("1.5")]
        [InlineData("abc")]
        public void Any_other_interval_is_refused_with_a_message(string input)
        {
            Assert.False(RepoWatchSettings.TryParseInterval(input, out int? minutes, out string error));
            Assert.Null(minutes);
            Assert.Contains("0, 5, 15, 30, 60", error, StringComparison.Ordinal);
            Assert.Contains(input.Trim(), error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("ClarionLive/multiterminal", "ClarionLive/multiterminal")]
        [InlineData("  a/b ,c/d  e/f\n", "a/b, c/d, e/f")]
        [InlineData("a/b,,c/d", "a/b, c/d")]
        [InlineData("Owner/Repo, owner/repo, OWNER/REPO", "Owner/Repo")]
        [InlineData("my-org/my_repo.js, x1/.github", "my-org/my_repo.js, x1/.github")]
        public void Repos_are_split_trimmed_deduplicated_and_joined(string input, string expected)
        {
            Assert.True(RepoWatchSettings.TryNormalizeRepos(input, out string normalized, out string error), error);
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" , ,\t")]
        public void An_empty_repo_list_is_null(string input)
        {
            Assert.True(RepoWatchSettings.TryNormalizeRepos(input, out string normalized, out string error), error);
            Assert.Null(normalized);
        }

        /// <summary>
        /// Falsified: dropping the owner-hyphen checks turned "-bad/x", "bad-/x" and "a--b/x" red; dropping the
        /// second-slash check turned only "a/b/c" red. The URL case does NOT pin the slash check: its owner
        /// part "https:" is already refused for the colon.
        /// </summary>
        [Theory]
        [InlineData("a/b/c", "a/b/c")]
        [InlineData("-bad/x", "-bad/x")]
        [InlineData("bad-/x", "bad-/x")]
        [InlineData("a--b/x", "a--b/x")]
        [InlineData("https://github.com/o/r", "https://github.com/o/r")]
        [InlineData("good/x, owner", "owner")]
        [InlineData("owner/", "owner/")]
        [InlineData("/repo", "/repo")]
        [InlineData("o/.", "o/.")]
        [InlineData("o/..", "o/..")]
        [InlineData("o_x/r", "o_x/r")]
        [InlineData("o/r#1", "o/r#1")]
        [InlineData("ö/r", "ö/r")]
        public void A_bad_entry_refuses_the_whole_list_and_is_named(string input, string badEntry)
        {
            Assert.False(RepoWatchSettings.TryNormalizeRepos(input, out string normalized, out string error));
            Assert.Null(normalized);
            Assert.Contains("'" + badEntry + "'", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Name_lengths_follow_GitHub_limits()
        {
            string owner39 = new string('a', 39);
            string repo100 = new string('r', 100);
            Assert.True(RepoWatchSettings.IsOwnerRepo(owner39 + "/" + repo100));
            Assert.False(RepoWatchSettings.IsOwnerRepo(owner39 + "a/r"));
            Assert.False(RepoWatchSettings.IsOwnerRepo("o/" + repo100 + "r"));
        }

        [Fact]
        public void Parse_reads_a_stored_list_and_skips_what_is_not_owner_repo()
        {
            Assert.Equal(new[] { "a/b", "c/d" }, RepoWatchSettings.ParseRepos("a/b, https://x/y/z, c/d, A/B"));
            Assert.Empty(RepoWatchSettings.ParseRepos(null));
            Assert.Empty(RepoWatchSettings.ParseRepos("  "));
        }
    }
}
