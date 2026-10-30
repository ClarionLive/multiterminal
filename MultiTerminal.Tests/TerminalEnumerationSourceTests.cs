#nullable enable
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// MainForm never reads DockPanelSuite's <c>Documents</c> list directly (GH #74). That list
    /// yields only DOCKED contents, so every direct read silently skips terminals in floating windows.
    /// Phase 1 of #74 moved about thirty such reads to <c>GridLayoutManager.GetAllTerminalDocuments</c>
    /// and missed one, written with <c>?.</c>, which a reviewer found. The few reads that really mean
    /// "docked only" (placement, grid presets, the no-layout restore) now go through
    /// <c>GetDockedTerminalDocuments</c>, so the choice is spelled out at every call site.
    ///
    /// <para>This is a source census, scanned over code with comments and string literals removed, so
    /// a comment naming the list (house style: "do not use X here") cannot trip it. Falsified both
    /// ways when written: a reintroduced <c>_dockPanel?.Documents</c> read turned it red, and a comment
    /// naming <c>_dockPanel.Documents</c> left it green.</para>
    /// </summary>
    public class TerminalEnumerationSourceTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string CodeOnly(string src)
        {
            string noStrings = Regex.Replace(src, "\"(?:\\\\.|[^\"\\\\\\n])*\"", "\"\"");
            string noBlocks = Regex.Replace(noStrings, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(noBlocks, @"//[^\n]*", string.Empty);
        }

        [Fact]
        public void MainForm_does_not_read_the_docked_only_Documents_list_directly()
        {
            string code = CodeOnly(File.ReadAllText(Path.Combine(RepoRoot(), "MainForm.cs")));
            Assert.True(code.Length > 100_000, $"MainForm.cs read as {code.Length} characters; the scan is broken, not the code.");

            var reads = Regex.Matches(code, @"\??\.\s*Documents\b");

            Assert.True(
                reads.Count == 0,
                "MainForm reads DockPanel.Documents directly, which skips terminals in floating windows. "
                + "Use _gridManager.GetAllTerminalDocuments(), or GetDockedTerminalDocuments() where only "
                + $"the docked area is meant. Found {reads.Count} read(s).");
        }
    }
}
