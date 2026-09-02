using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 2289bb8a — <c>TerminalTheme</c> is a class whose <c>Dark</c>/<c>Light</c> accessors
    /// allocate, so <c>==</c> against them is ALWAYS false.
    ///
    /// <para>HOW THIS SURFACED. The Attention panel opened in light mode with the app set to dark,
    /// stayed light, and corrected itself the moment the user toggled the theme from the menu. The
    /// panel was blameless. <c>MainForm</c> opened it with:</para>
    ///
    /// <code>_attentionPanel.ApplyTheme(_currentTheme == TerminalTheme.Dark);</code>
    ///
    /// <para><see cref="TerminalTheme"/> is a <b>class</b>, <c>Dark</c> and <c>Light</c> are
    /// expression-bodied static properties that return <c>new TerminalTheme { ... }</c> on every
    /// access, and the type overloads neither <c>operator ==</c> nor <c>Equals</c>. So that
    /// expression is a reference comparison against an object allocated microseconds earlier: it can
    /// never be true. Every such call site passed <c>false</c> — "not dark" — unconditionally. The
    /// theme-toggle path used <c>_currentTheme.IsDark</c> instead, which is why toggling fixed it and
    /// why the bug looked like a race rather than the constant it actually was.</para>
    ///
    /// <para>There were 23 of these, and they were not confined to the new panel: Inbox, File
    /// Preview, Chat, Activity, Office, Tasks, Profile and Attention all opened light whatever the
    /// theme, and <c>WebViewTerminalRenderer</c> had the same defect pointing the other way
    /// (<c>theme == TerminalTheme.Light ? "light" : "dark"</c>, always false, so terminals always
    /// rendered dark).</para>
    ///
    /// <para>WHY A SOURCE SCAN AND NOT A BEHAVIOURAL TEST. The defect is invisible at every level a
    /// normal test can see. It compiles without a warning, the types match, the call site reads
    /// correctly in review, and no runtime assertion inside the panel can tell "the host believes
    /// light" from "the host is dark and told me light" — the panel is being handed a plain
    /// <c>bool</c> and is obeying it exactly. What is wrong is the SHAPE of the expression, so the
    /// shape is what gets pinned. This follows BoardHudDoorwayTests and DashboardHeaderDoorwayTests,
    /// which pin compiler-invisible string contracts the same way.</para>
    ///
    /// <para>The scan is deliberately repo-wide rather than MainForm-only. Restricting it to the file
    /// where the bug happened to live would be the same mistake as pinning <c>btn-attention</c>
    /// instead of the general doorway: it would pass while the next author reintroduced the identical
    /// expression one file over.</para>
    /// </summary>
    public class TerminalThemeComparisonTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        /// <summary>Enumerates the app's own C# sources, skipping build output and this test project.</summary>
        private static IEnumerable<string> ProductionCsFiles()
        {
            string root = RepoRoot();
            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p =>
                {
                    string rel = Path.GetRelativePath(root, p).Replace('\\', '/');
                    return !rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                        && !rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                        && !rel.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                        && !rel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                        && !rel.StartsWith("MultiTerminal.Tests/", StringComparison.OrdinalIgnoreCase)
                        && !rel.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase);
                });
        }

        /// <summary>Strips block comments and whole-line // comments, so prose cannot trip the scan.</summary>
        private static string ReadStripped(string path)
        {
            string src = File.ReadAllText(path);
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        /// <summary>
        /// The invariant that would have caught the bug: nothing compares a
        /// <see cref="TerminalTheme"/> to <c>TerminalTheme.Dark</c> or <c>TerminalTheme.Light</c>
        /// with <c>==</c> or <c>!=</c>. Ask <c>.IsDark</c> instead.
        /// </summary>
        [Fact]
        public void No_source_file_compares_a_TerminalTheme_by_reference()
        {
            var pattern = new Regex(@"(==|!=)\s*TerminalTheme\s*\.\s*(Dark|Light)\b");
            var offenders = new List<string>();

            foreach (string file in ProductionCsFiles())
            {
                string[] lines = ReadStripped(file).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "TerminalTheme.Dark/Light allocate a NEW instance on every access and the type "
                + "overloads neither operator== nor Equals, so these comparisons are ALWAYS false "
                + "and silently pass the wrong theme. Use `theme.IsDark` instead:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The premise of the test above, asserted rather than assumed. If someone later makes
        /// <c>Dark</c>/<c>Light</c> cached singletons or adds an equality overload, <c>==</c> stops
        /// being a defect and this test goes red to say the scan above has become unnecessary —
        /// rather than the scan quietly outliving its reason.
        /// </summary>
        [Fact]
        public void Dark_does_not_equal_Dark_which_is_why_the_scan_exists()
        {
            Assert.False(
                TerminalTheme.Dark == TerminalTheme.Dark,
                "TerminalTheme.Dark == TerminalTheme.Dark is now TRUE. Either the accessors became "
                + "singletons or equality was overloaded. Reference comparison is no longer a bug, "
                + "so No_source_file_compares_a_TerminalTheme_by_reference can be retired.");

            Assert.True(TerminalTheme.Dark.IsDark, "TerminalTheme.Dark.IsDark must be true.");
            Assert.False(TerminalTheme.Light.IsDark, "TerminalTheme.Light.IsDark must be false.");
        }
    }
}
