using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Every persist string a dockable pane declares must be one the layout loader answers to
    /// (task edcdcdd5, second pass).
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS FILE EXISTS. The owner closed MultiTerminal with the Attention pane docked, reopened
    /// it, and the pane was gone. The pane itself was fine: it declared
    /// <c>GetPersistString() =&gt; "AttentionPanel"</c> and MainForm saved it faithfully. But
    /// <c>MainForm.GetContentFromPersistString</c> — the callback DockPanel Suite uses to re-bind a
    /// saved entry to a live control — is a hand-written <c>if (persistString == "...")</c> chain,
    /// and nobody had added a case. The loader got null and dropped the pane, silently, on every
    /// restart. The <c>RestoreSinglePanel</c> path that DOES know about the pane only runs when
    /// there is no layout file at all — i.e. never, on a machine that has been used once.
    /// </para>
    /// <para>
    /// This is the FOURTH time a hand-written per-panel roster in MainForm has drifted (after the
    /// HUD theme chain, the zoom chain, and the two theming lists). As with
    /// <c>PanelThemingParityTests</c>, the guard is deliberately not "assert Attention is in the
    /// list": the panes are made to check the loader, so pane thirteen is covered for free.
    /// </para>
    /// <para>
    /// WHAT IS NOT COVERED: this proves the loader has a case for each declared string, not that
    /// the case constructs and wires the pane correctly. That still needs the running app.
    /// </para>
    /// </remarks>
    public class PanelPersistRosterTests
    {
        /// <summary>
        /// Persist strings that are declared but deliberately NOT restored from layout.
        /// </summary>
        /// <remarks>
        /// <c>AgentPanel</c> is the live transcript of one subagent, bound to a process that does
        /// not survive a restart; there is nothing to re-bind it to. If that ever changes, delete
        /// it from here and the test will demand a loader case.
        /// </remarks>
        private static readonly HashSet<string> NotRestoredByDesign = new HashSet<string>(StringComparer.Ordinal)
        {
            "AgentPanel",
        };

        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string BodyOf(string src, string anchor, string what)
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

            Assert.Fail($"Unbalanced braces while extracting {what}.");
            return string.Empty;
        }

        /// <summary>
        /// Every literal persist string declared by a production <c>GetPersistString</c> override,
        /// with the file that declares it. Matches both the expression-bodied and block forms.
        /// Non-literal declarations (<c>typeof(X).FullName</c>) are out of scope.
        /// </summary>
        private static Dictionary<string, string> DeclaredPersistStrings()
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var pattern = new Regex(
                @"GetPersistString\s*\(\s*\)\s*(?:=>\s*|\{[^}]*?return\s+)""([A-Za-z]+)""",
                RegexOptions.Singleline);

            foreach (var file in Directory.EnumerateFiles(RepoRoot(), "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(RepoRoot(), file);
                if (rel.StartsWith(".claude", StringComparison.Ordinal)) continue;
                if (rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                if (rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                if (rel.StartsWith("MultiTerminal.Tests", StringComparison.Ordinal)) continue;

                foreach (Match m in pattern.Matches(StripComments(File.ReadAllText(file))))
                {
                    found[m.Groups[1].Value] = rel;
                }
            }

            return found;
        }

        [Fact]
        public void Every_declared_persist_string_has_a_loader_case()
        {
            var declared = DeclaredPersistStrings();
            Assert.True(declared.Count >= 8, $"Expected to find the panes' persist strings; found {declared.Count}. The scan is broken, not the code.");

            string mainForm = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "MainForm.cs")));
            string loader = BodyOf(mainForm, "IDockContent GetContentFromPersistString(", "GetContentFromPersistString");

            var missing = declared
                .Where(kv => !NotRestoredByDesign.Contains(kv.Key))
                .Where(kv => !loader.Contains($"\"{kv.Key}\"", StringComparison.Ordinal))
                .Select(kv => $"\"{kv.Key}\" (declared in {kv.Value})")
                .ToList();

            Assert.True(
                missing.Count == 0,
                "These panes save a persist string the layout loader does not recognise, so they are "
                + "silently dropped on every restart. Add a case to MainForm.GetContentFromPersistString:\n  "
                + string.Join("\n  ", missing));
        }

        /// <summary>
        /// The exclusion list must not rot into a place where real panes get parked.
        /// </summary>
        [Fact]
        public void Every_by_design_exclusion_is_still_declared_somewhere()
        {
            var declared = DeclaredPersistStrings();
            foreach (var name in NotRestoredByDesign)
            {
                Assert.True(declared.ContainsKey(name), $"\"{name}\" is excluded but no pane declares it any more; remove it from NotRestoredByDesign.");
            }
        }
    }
}
