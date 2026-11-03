using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 37edc158: the pane header shows the Claude profile only when it is not Default.
    /// The rule is pure and tested directly. The message name between TerminalStatusBarRenderer and
    /// statusbar.html is a string contract no compiler checks, so it is pinned here too, with comments
    /// stripped first so prose naming it cannot satisfy it. Not covered: that the page renders it (no
    /// browser runs in the suite) and that each MainForm launch path reaches StartTerminal.
    /// </summary>
    public class PaneProfileBadgeTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Default")]
        [InlineData("default")]
        [InlineData("  DEFAULT ")]
        public void Default_blank_and_no_launch_show_nothing(string name)
        {
            Assert.Null(PaneProfileBadge.TextFor(name));
        }

        [Theory]
        [InlineData("Work", "Work")]
        [InlineData("  Work  ", "Work")]
        [InlineData("<b>x</b>", "<b>x</b>")]
        public void A_named_profile_shows_its_trimmed_name_unchanged(string name, string expected)
        {
            Assert.Equal(expected, PaneProfileBadge.TextFor(name));
        }

        private static string Source(string relative, [CallerFilePath] string thisFile = "")
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));
            string path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"Could not locate '{path}'.");
            string src = File.ReadAllText(path);
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                src = Regex.Replace(src, @"<!--.*?-->", " ", RegexOptions.Singleline);
                src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            }
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        [Fact]
        public void The_renderer_and_the_page_use_the_same_message_name_and_prefix_length()
        {
            string renderer = Source(Path.Combine("Controls", "TerminalStatusBarRenderer.cs"));
            string page = Source(Path.Combine("Controls", "TerminalStatusBar", "statusbar.html"));

            Assert.True(renderer.Length > 2000 && page.Length > 2000, "A source was not read.");
            // Sent live and replayed on ready: two sites.
            Assert.Equal(2, Regex.Count(renderer, Regex.Escape("$\"claudeProfile:{_lastClaudeProfile}\"")));
            Assert.Contains("data.startsWith('claudeProfile:')", page);
            int len = "claudeProfile:".Length;
            Assert.Contains($"setClaudeProfile(data.substring({len}))", page);
        }

        [Fact]
        public void The_page_sets_the_free_text_name_with_textContent_never_as_html()
        {
            string page = Source(Path.Combine("Controls", "TerminalStatusBar", "statusbar.html"));
            Match m = Regex.Match(page, @"function setClaudeProfile\(name\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);

            Assert.True(m.Success, "setClaudeProfile was not found.");
            string body = m.Groups["body"].Value;
            Assert.Contains(".textContent = ", body);
            Assert.DoesNotContain("innerHTML", body);
            Assert.DoesNotContain("insertAdjacentHTML", body);
        }
    }
}
