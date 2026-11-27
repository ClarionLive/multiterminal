using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket a923589f, Owner's live test: three repositories wrapped the header's GitHub row, and the row
    /// was clipped because the page's html, body and container were fixed at 140px, however tall the bar
    /// grew. This pins that those three rules set no fixed height. Comments are stripped first. Not covered:
    /// how the page actually lays out (no browser runs in the suite); that was checked by rendering it.
    /// </summary>
    public class StatusBarGitHubRowLayoutTests
    {
        private static string Page([CallerFilePath] string thisFile = "")
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));
            string src = File.ReadAllText(Path.Combine(root, "Controls", "TerminalStatusBar", "statusbar.html"));
            src = Regex.Replace(src, @"<!--.*?-->", " ", RegexOptions.Singleline);
            return Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        }

        private static string RuleBody(string page, string selector)
        {
            var match = Regex.Match(page, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
            Assert.True(match.Success, $"The rule '{selector}' was not found.");
            return match.Groups["body"].Value;
        }

        [Theory]
        [InlineData("html, body")]
        [InlineData(".status-bar-container")]
        public void The_page_can_grow_past_140px(string selector)
        {
            string body = RuleBody(Page(), selector);

            Assert.Contains("min-height: 140px", body);
            // A bare height, not min-height: the lookbehind stops "min-height" matching.
            Assert.DoesNotMatch(@"(?<![-\w])height\s*:", body);
        }

        [Fact]
        public void The_github_mark_has_its_own_column_so_it_cannot_wrap_away_from_the_entries()
        {
            string body = RuleBody(Page(), ".row-github");

            Assert.Contains("display: grid", body);
            Assert.DoesNotContain("flex-wrap", body);
        }
    }
}
