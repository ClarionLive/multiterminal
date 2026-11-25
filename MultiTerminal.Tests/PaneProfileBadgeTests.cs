using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiTerminal.Models;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket 37edc158: the pane header shows the Claude profile a pane effectively runs under, and nothing
    /// for plain Default. The rule is pure and tested directly. The message name between
    /// TerminalStatusBarRenderer and statusbar.html, and where the badge sits on the page, are string
    /// contracts no compiler checks, so they are pinned here too, with comments stripped first so prose
    /// cannot satisfy them. Not covered: that the page renders it (no browser runs in the suite) and that
    /// each MainForm launch path reaches StartTerminal.
    /// </summary>
    public class PaneProfileBadgeTests
    {
        private const string Home = @"C:\Users\Tester";

        private static ClaudeProfileContext Ctx(string inherited, params ClaudeProfile[] configured) =>
            new ClaudeProfileContext(configured, inherited, Home, _ => true);

        private static readonly ClaudeProfile Work = new ClaudeProfile("Work", @"C:\Users\Tester\.claude-work");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Default")]
        [InlineData("default")]
        [InlineData("  DEFAULT ")]
        public void Default_blank_and_no_launch_with_nothing_inherited_show_nothing(string name)
        {
            Assert.Null(PaneProfileBadge.For(name, Ctx(null, Work)));
            Assert.Null(PaneProfileBadge.For(name, Ctx("  ", Work)));
        }

        [Theory]
        [InlineData("Work", "Work")]
        [InlineData("  Work  ", "Work")]
        [InlineData("<b>x</b>", "<b>x</b>")]
        public void A_named_profile_shows_its_trimmed_name_unchanged(string name, string expected)
        {
            var badge = PaneProfileBadge.For(name, Ctx(null, Work));
            Assert.Equal(expected, badge.Text);
            Assert.Contains(expected, badge.Tooltip);
        }

        [Fact]
        public void A_named_profile_shows_its_name_even_when_MT_inherited_a_folder()
        {
            Assert.Equal("Work", PaneProfileBadge.For("Work", Ctx(@"D:\elsewhere", Work)).Text);
        }

        [Fact]
        public void Default_with_an_inherited_folder_that_is_a_configured_profiles_shows_that_profile()
        {
            // Different spelling of the same folder: case, trailing separator, forward slashes.
            var badge = PaneProfileBadge.For("Default", Ctx(@"c:/users/TESTER/.claude-work/", Work));

            Assert.Equal("Work", badge.Text);
            Assert.Contains("CLAUDE_CONFIG_DIR", badge.Tooltip);
        }

        [Fact]
        public void Default_with_an_unmatched_inherited_folder_shows_a_label_and_the_folder_as_tooltip()
        {
            var badge = PaneProfileBadge.For(null, Ctx(@"D:\Other\claude-home\", Work));

            Assert.Equal(PaneProfileBadge.InheritedLabel, badge.Text);
            Assert.Contains(@"D:\Other\claude-home", badge.Tooltip);
        }

        [Fact]
        public void Default_with_the_home_claude_folder_inherited_shows_nothing_because_that_is_Defaults_own_folder()
        {
            Assert.Null(PaneProfileBadge.For(null, Ctx(@"C:\Users\Tester\.claude")));
        }

        [Fact]
        public void The_wire_form_carries_the_tooltip_as_well_as_the_text_and_null_means_nothing()
        {
            Assert.Equal("null", PaneProfileBadge.ToWire(null));

            var badge = PaneProfileBadge.For(null, Ctx(@"D:\Other\claude-home"));
            using var doc = JsonDocument.Parse(PaneProfileBadge.ToWire(badge));
            Assert.Equal(badge.Text, doc.RootElement.GetProperty("text").GetString());
            Assert.Equal(badge.Tooltip, doc.RootElement.GetProperty("tooltip").GetString());
        }

        private static string Source(string relative, [CallerFilePath] string thisFile = "")
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));
            string path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"Could not locate '{path}'.");
            string src = File.ReadAllText(path);
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                src = Regex.Replace(src, @"<!--.*?-->", " ", RegexOptions.Singleline);
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string RendererSource() => Source(Path.Combine("Controls", "TerminalStatusBarRenderer.cs"));
        private static string PageSource() => Source(Path.Combine("Controls", "TerminalStatusBar", "statusbar.html"));

        [Fact]
        public void The_renderer_and_the_page_use_the_same_message_name_and_prefix_length()
        {
            string renderer = RendererSource();
            string page = PageSource();

            Assert.True(renderer.Length > 2000 && page.Length > 2000, "A source was not read.");
            // Sent live and replayed on ready: two sites, both from the stored JSON (text AND tooltip).
            Assert.Equal(2, Regex.Count(renderer, Regex.Escape("$\"claudeProfile:{_lastClaudeProfileJson}\"")));
            Assert.Contains("data.startsWith('claudeProfile:')", page);
            int len = "claudeProfile:".Length;
            Assert.Contains($"setClaudeProfile(JSON.parse(data.substring({len})))", page);
            Assert.Contains("_lastClaudeProfileJson = Services.PaneProfileBadge.ToWire(badge);", renderer);
        }

        [Fact]
        public void The_page_sets_the_free_text_with_textContent_never_as_html()
        {
            string page = PageSource();
            Match m = Regex.Match(page, @"function setClaudeProfile\(badge\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);

            Assert.True(m.Success, "setClaudeProfile was not found.");
            string body = m.Groups["body"].Value;
            Assert.Contains(".textContent = ", body);
            Assert.Contains(".title = ", body);
            Assert.DoesNotContain("innerHTML", body);
            Assert.DoesNotContain("insertAdjacentHTML", body);
        }

        [Fact]
        public void The_badge_sits_in_the_identity_row_which_is_visible_from_launch_not_in_a_hidden_row()
        {
            string page = PageSource();

            int identity = page.IndexOf("<div class=\"row-identity\">", StringComparison.Ordinal);
            int badge = page.IndexOf("id=\"sl-profile\"", StringComparison.Ordinal);
            int folderRow = page.IndexOf("id=\"row-folder\"", StringComparison.Ordinal);

            Assert.True(identity > 0 && badge > 0 && folderRow > 0, "A landmark was not found.");
            Assert.Equal(1, Regex.Count(page, "id=\"sl-profile\""));
            // Rows 2-4 start hidden and only a status line or GitHub update un-hides them (Codex and
            // PowerShell panes never get one), so the badge must come before the first of them.
            Assert.True(identity < badge && badge < folderRow,
                "The profile badge must sit in the identity row, before the hidden row-folder.");
        }
    }
}
