using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Grid Layout adds Home tabs, not named PowerShells (task a7d3e675).
    /// <para><c>MainForm.ApplyGridLayout</c> runs through WinForms docking and WebView2, so it cannot be
    /// driven from a unit test. What these facts pin is its shape: the top-up creates panes only through
    /// <c>AddNewTerminal</c> with no launch parameter (which is what makes <c>AddNewTerminal</c> show the
    /// start screen and skip registration), and nothing in the method registers or starts a terminal.</para>
    /// <para>CENSUS DISCIPLINE (verification-discipline.md): the scan is scoped to the
    /// <c>ApplyGridLayout</c> body, not the file. Both whole-line and trailing comments are stripped,
    /// because the method's own comment names <c>StartTerminal</c> while explaining why it is gone. The
    /// extraction is asserted against two anchors that only that method contains. The method holds no
    /// string literals, so stripping a trailing <c>//</c> cannot cut real code.</para>
    /// <para>Falsified on 2026-10-04 by restoring the old behaviour in MainForm.cs and running these
    /// facts. Each one went red, and each went green again after the restore:
    /// (1) passing <c>_settings?.GetLastDirectory()</c> as AddNewTerminal's first argument turned
    /// <see cref="Top_up_creates_panes_only_through_AddNewTerminal_with_no_launch_parameter"/> red;
    /// (2) putting the old block back in the loop (<c>new TerminalDocument()</c>, <c>Show</c>,
    /// <c>PreRegisterTerminal</c>, <c>StartTerminal</c>) turned
    /// <see cref="ApplyGridLayout_never_registers_constructs_or_starts_a_terminal_itself"/> red. Only
    /// the combined block was run, so it is proven that the fact catches the old code. It is NOT proven
    /// that each of its four assertions would catch its own call on its own;
    /// (3) deleting the restore line turned
    /// <see cref="The_users_last_active_terminal_is_restored_after_the_top_up"/> red.
    /// In each run the other two facts stayed green.</para>
    /// </summary>
    public class GridLayoutHomePaneTests
    {
        [Fact]
        public void Top_up_creates_panes_only_through_AddNewTerminal_with_no_launch_parameter()
        {
            string body = ApplyGridLayoutBody();

            var calls = Regex.Matches(body, @"AddNewTerminal\s*\(([^)]*)\)");
            Assert.True(calls.Count > 0,
                "ApplyGridLayout no longer calls AddNewTerminal. Layout panes must be created through the " +
                "same path as \"+ New\" so they open on the start screen (task a7d3e675).");

            foreach (Match call in calls)
            {
                string args = Regex.Replace(call.Groups[1].Value, @"\s+", " ").Trim();
                Assert.True(args == "forceTabMode: true",
                    $"ApplyGridLayout calls AddNewTerminal({args}). The only argument allowed is " +
                    "forceTabMode: true. Any directory, identity, command or project argument makes " +
                    "AddNewTerminal register a name and start a shell, which is the reported bug.");
            }
        }

        [Fact]
        public void ApplyGridLayout_never_registers_constructs_or_starts_a_terminal_itself()
        {
            string body = ApplyGridLayoutBody();

            Assert.DoesNotContain("new TerminalDocument", body);
            Assert.DoesNotContain("PreRegisterTerminal", body);
            Assert.DoesNotContain("RegisterTerminal(", body);
            Assert.DoesNotContain("StartTerminal(", body);
        }

        [Fact]
        public void The_users_last_active_terminal_is_restored_after_the_top_up()
        {
            string body = ApplyGridLayoutBody();

            int save = body.IndexOf("var previousLastActive = _lastActiveTerminal;", StringComparison.Ordinal);
            int lastCreate = body.LastIndexOf("AddNewTerminal(", StringComparison.Ordinal);
            int restore = body.IndexOf("_lastActiveTerminal = previousLastActive;", StringComparison.Ordinal);

            Assert.True(save >= 0, "ApplyGridLayout does not save _lastActiveTerminal before the top-up.");
            Assert.True(lastCreate > save, "The save must come before the panes are created.");
            Assert.True(restore > lastCreate,
                "ApplyGridLayout does not restore _lastActiveTerminal after creating panes. AddNewTerminal " +
                "makes each new Home pane the last active terminal, so Recent-folder cd would go to an " +
                "empty pane instead of the user's terminal.");
        }

        /// <summary>The ApplyGridLayout body, comments removed, extraction asserted.</summary>
        private static string ApplyGridLayoutBody()
        {
            string src = File.ReadAllText(MainFormPath());
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            src = string.Join("\n",
                src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            const string signature = "private void ApplyGridLayout(GridLayoutManager.GridPreset preset)";
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "ApplyGridLayout's signature was not found in MainForm.cs.");
            Assert.True(src.IndexOf(signature, start + 1, StringComparison.Ordinal) < 0,
                "ApplyGridLayout's signature appears twice; the extraction would be ambiguous.");

            int open = src.IndexOf('{', start);
            string body = BraceBody(src, open);

            // Trailing comments inside the method (no string literals there to cut).
            body = Regex.Replace(body, @"//[^\n]*", string.Empty);

            Assert.True(body.Length > 300, $"Extracted ApplyGridLayout body is only {body.Length} chars.");
            Assert.Contains("GetTerminalCountForPreset(preset)", body);
            Assert.Contains("_gridManager.ApplyPreset(preset)", body);
            return body;
        }

        private static string BraceBody(string src, int open)
        {
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src[open..(i + 1)];
                }
            }

            Assert.Fail($"Braces never balanced from offset {open}.");
            return string.Empty;
        }

        private static string MainFormPath([CallerFilePath] string thisFile = "")
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "MainForm.cs"));
            Assert.True(File.Exists(path), $"Could not locate MainForm.cs at '{path}'.");
            return path;
        }
    }
}
