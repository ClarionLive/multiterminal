using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Ticket b0d2f090: a Fable session's model badge stands out in the pane header. The class name is a
    /// string contract between the page's CSS and its script that no compiler checks, so this pins that both
    /// sides use the same name. Comments are stripped first, so prose naming the class cannot satisfy it.
    /// Not covered: that the page actually renders it (no browser runs in the suite).
    /// </summary>
    public class StatusBarFableStyleTests
    {
        private static string Page([CallerFilePath] string thisFile = "")
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));
            string path = Path.Combine(root, "Controls", "TerminalStatusBar", "statusbar.html");
            Assert.True(File.Exists(path), $"Could not locate '{path}'.");
            string src = File.ReadAllText(path);
            src = Regex.Replace(src, @"<!--.*?-->", " ", RegexOptions.Singleline);
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        [Fact]
        public void The_css_rule_and_the_script_toggle_use_the_same_class()
        {
            string page = Page();

            Assert.True(page.Length > 2000, "The page was not read.");
            Assert.Contains(".sl-model.sl-model-fable", page);
            Assert.Contains("classList.toggle('sl-model-fable'", page);
            Assert.Matches(@"/fable/i\.test\(model", page);
        }
    }
}
