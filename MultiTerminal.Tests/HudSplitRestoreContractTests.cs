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
    /// Guards every splitter-restore site against one specific defect: a bounds test that the clamp
    /// immediately above it has already made impossible.
    /// </summary>
    /// <remarks>
    /// <para>The shape is:</para>
    /// <code>
    /// if (distance > maxDistance) distance = maxDistance;
    /// if (distance > Panel1MinSize &amp;&amp; distance &lt; maxDistance)   // can NEVER be true
    /// </code>
    /// <para>
    /// After the clamp, <c>distance == maxDistance</c>, so the second comparison compares a value
    /// with itself. Every saved ratio at or above the clamp restores nothing. Where the latch and
    /// the SplitterMoved subscription live inside that branch, persistence never opens either — the
    /// position is forgotten on open and forgotten again on close.
    /// </para>
    /// <para>
    /// This test exists in its own file, scanning BOTH documents, because the defect spread by
    /// copy-paste between siblings. Task f5744489 fixed it in the board and added a per-file guard;
    /// that guard missed this exact instance because TerminalDocument writes the comparison in the
    /// inlined <c>Height - Panel2MinSize</c> spelling rather than as <c>maxDistance</c>. A per-file,
    /// per-spelling guard demonstrably does not hold this line — hence one test, both files, both
    /// spellings.
    /// </para>
    /// <para>
    /// It keys on the INVARIANT — a <c>&lt; max</c> comparison that follows a clamp assignment in
    /// the same block — not on a literal string. That matters in both directions: it must catch the
    /// dead test however it is written, and it must NOT fire on the agent-splitter sites, where no
    /// clamp precedes the comparison and the bounds check is genuine and load-bearing.
    /// </para>
    /// <para>
    /// Setting <c>distance == maxDistance</c> is safe: WinForms silently clamps SplitterDistance
    /// down to <c>Height - Panel2MinSize - SplitterWidth</c> rather than throwing. Measured against
    /// a real SplitContainer mirroring the terminal HUD's geometry, not assumed.
    /// </para>
    /// </remarks>
    public class HudSplitRestoreContractTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));

        private static string ReadStripped(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            string src = File.ReadAllText(path);

            // Strip line comments so the explanatory notes at each fixed site — which necessarily
            // quote the removed comparison — cannot fail this test.
            return string.Join("\n", src
                .Split('\n')
                .Select(line =>
                {
                    int idx = line.IndexOf("//", StringComparison.Ordinal);
                    return idx >= 0 ? line.Substring(0, idx) : line;
                }));
        }

        public static IEnumerable<object[]> RestoreFiles => new[]
        {
            new object[] { Path.Combine("Docking", "TerminalDocument.cs") },
            new object[] { Path.Combine("TasksPanel", "TasksPanelDocument.cs") },
        };

        /// <summary>
        /// No restore site may gate its assignment on a comparison the preceding clamp forecloses.
        /// </summary>
        [Theory]
        [MemberData(nameof(RestoreFiles))]
        public void No_restore_site_tests_a_bound_the_clamp_already_forced(string relativePath)
        {
            string src = ReadStripped(relativePath.Split(Path.DirectorySeparatorChar));

            // Find each clamp assignment, then look only at the text between it and the assignment
            // it guards. A `< max` comparison inside that window is dead by construction.
            var clamps = Regex.Matches(src, @"distance\s*=\s*maxDistance\s*;");
            Assert.True(
                clamps.Count > 0,
                $"{relativePath} has no 'distance = maxDistance' clamp at all. Either the restore " +
                "sites were renamed or the clamp was dropped — both change what this test guards, " +
                "so re-read the file rather than deleting this assertion.");

            foreach (Match clamp in clamps)
            {
                int start = clamp.Index + clamp.Length;
                int assign = src.IndexOf("SplitterDistance = distance", start, StringComparison.Ordinal);
                if (assign < 0) continue;

                string window = src.Substring(start, assign - start);

                // Both spellings of the same expression: the local `maxDistance`, and the inlined
                // `Height - Panel2MinSize` / `Width - Panel2MinSize` form.
                bool dead =
                    Regex.IsMatch(window, @"distance\s*<\s*maxDistance") ||
                    Regex.IsMatch(window, @"distance\s*<\s*[^;{}]*Panel2MinSize") ||
                    Regex.IsMatch(window, @"maxDistance\s*>\s*distance");

                Assert.False(
                    dead,
                    $"{relativePath}: a restore site gates its SplitterDistance assignment on a " +
                    "'distance < max' comparison that the clamp immediately above has already " +
                    "forced to be false. That branch can never be taken, so every saved ratio at " +
                    "or above the clamp restores nothing — and where the latch and SplitterMoved " +
                    "hook sit in the same branch, persistence never opens either. Test " +
                    $"'> Panel1MinSize' only. Offending region:\n{window.Trim()}");
            }
        }

        /// <summary>
        /// The agent-splitter sites must keep their bounds test — there is no clamp above them.
        /// </summary>
        /// <remarks>
        /// The counterpart to the test above, and the reason that one keys on the clamp rather than
        /// on the comparison. `_terminalAgentSplitter` restores compute a distance and test it
        /// against both bounds with nothing clamping first, so the `&lt;` there is live: deleting it
        /// would push an out-of-range distance at WinForms. A future reader "cleaning up" to match
        /// the HUD sites would break this, and nothing else would notice.
        /// </remarks>
        [Fact]
        public void The_agent_splitter_keeps_its_bounds_test_because_nothing_clamps_first()
        {
            string src = ReadStripped("Docking", "TerminalDocument.cs");

            var guarded = Regex.Matches(
                src,
                @"distance\s*>\s*_terminalAgentSplitter\.Panel1MinSize\s*&&\s*distance\s*<\s*_terminalAgentSplitter\.Width\s*-\s*_terminalAgentSplitter\.Panel2MinSize");

            Assert.True(
                guarded.Count >= 3,
                $"Expected the three agent-splitter restores to keep testing BOTH bounds; found " +
                $"{guarded.Count}. No clamp precedes them, so the '<' test is live and removing it " +
                "would hand WinForms an out-of-range SplitterDistance. This is the case that stops " +
                "the sibling test above from being applied blindly to every '<' comparison.");
        }
    }
}
