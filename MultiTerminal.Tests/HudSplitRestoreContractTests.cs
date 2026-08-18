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
    /// Guards every splitter-restore site against two related defects: discarding a restore because
    /// of a bound the clamp above has already forced, and latching the one-shot restore flag on a
    /// restore that only fitted because it was clamped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original shape was <c>if (distance > maxDistance) distance = maxDistance;</c> followed by
    /// <c>if (distance > Panel1MinSize &amp;&amp; distance &lt; maxDistance)</c>. Note carefully what is and
    /// is not true of that second test: it is NOT dead in general — it is true whenever the saved
    /// ratio fits at the current height. It is guaranteed false only in the CLAMPED case. That
    /// distinction is the whole subject of this file, and getting it wrong in either direction
    /// causes a real defect:
    /// </para>
    /// <list type="bullet">
    /// <item>Leaving the comparison in discards the restore whenever the ratio does not fit, so a
    /// saved position at the bottom of its range never comes back.</item>
    /// <item>Removing the comparison outright ALSO removes the accidental "only latch if it fits"
    /// gate, so the handler latches on a clamped restore taken at a transient docking height, the
    /// early return then blocks the correct restore at the final height, and FixedPanel.Panel2
    /// pins the HUD at its minimum — after which the bogus ratio is persisted and broadcast.</item>
    /// </list>
    /// <para>
    /// The correct shape applies the clamped value but latches only on an unclamped fit. Both
    /// halves are asserted here, because the review that caught the second defect also observed
    /// that a test checking only the comparison would not have caught it.
    /// </para>
    /// <para>
    /// This file scans BOTH documents rather than living beside one of them, because the defect
    /// spread by copy-paste between siblings. Task f5744489 fixed it in the board and added a
    /// per-file guard; that guard missed the TerminalDocument instance because it matched only the
    /// <c>maxDistance</c> spelling and not the inlined <c>Height - Panel2MinSize</c> one.
    /// </para>
    /// <para>
    /// Note the overlap: <c>BoardHudDoorwayTests</c> also asserts the comparison invariant for
    /// TasksPanelDocument, with a tighter window. That guard is deliberately left in place — it is
    /// scoped to one method and fails with a board-specific message — while this one exists to
    /// catch the same shape appearing at ANY restore site in either file.
    /// </para>
    /// <para>
    /// Setting <c>distance == maxDistance</c> is safe: WinForms silently clamps SplitterDistance to
    /// the true ceiling rather than throwing. Measured against a real SplitContainer mirroring the
    /// terminal HUD's geometry — at height 461 with Panel2MinSize 80 and SplitterWidth 6, setting
    /// 381 was accepted and clamped to 375.
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

            // Whole-line `//` only, matching BoardHudDoorwayTests' deliberate rule: truncating at
            // the first `//` anywhere would mangle a `https://` inside a string literal. Block
            // comments are stripped too, since TerminalDocument uses them inline.
            src = Regex.Replace(src, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return string.Join("\n", src
                .Split('\n')
                .Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));
        }

        /// <summary>
        /// Returns the source from <paramref name="start"/> to the end of the block enclosing it.
        /// </summary>
        /// <remarks>
        /// Bounding the scan by brace depth rather than by "the next assignment anywhere in the
        /// file" matters: an unbounded window can run from one method into a later one and fail on
        /// an innocent site. Specifically it could reach ApplyAgentSplitRatio, whose `&lt;` bound IS
        /// live, and blame it for a defect that lives elsewhere.
        /// </remarks>
        private static string EnclosingBlockFrom(string src, int start)
        {
            int depth = 0;
            for (int i = start; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    if (depth == 0) return src.Substring(start, i - start);
                    depth--;
                }
            }

            return src.Substring(start);
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

            // Matches the direct assignment AND the Math.Min refactor of it. A clamp spelled a way
            // this pattern misses would not merely go unchecked — it would leave the whole site
            // unscanned while the test still passed on the strength of the others.
            var clamps = Regex.Matches(src, @"distance\s*=\s*(maxDistance|Math\.Min\([^;]*maxDistance[^;]*\))\s*;");
            Assert.True(
                clamps.Count > 0,
                $"{relativePath} contains no recognisable 'distance = maxDistance' clamp. Either the " +
                "restore sites were renamed or the clamp was respelled — both change what this test " +
                "guards, so re-read the file rather than deleting this assertion.");

            foreach (Match clamp in clamps)
            {
                int start = clamp.Index + clamp.Length;
                string block = EnclosingBlockFrom(src, start);

                // Tolerate spacing variations; do NOT silently skip when absent. A clamp with no
                // assignment in its own block means this test has stopped guarding that site, and a
                // guard that quietly stops guarding is the exact failure class this file exists for.
                Match assign = Regex.Match(block, @"SplitterDistance\s*=\s*distance\s*;");
                Assert.True(
                    assign.Success,
                    $"{relativePath}: found a clamp at offset {clamp.Index} with no " +
                    "'SplitterDistance = distance;' in its enclosing block. This test can no longer " +
                    "see what that clamp guards — fix the scan rather than letting the site go " +
                    $"unchecked. Block was:\n{block.Trim()}");

                string window = block.Substring(0, assign.Index);

                bool dead =
                    Regex.IsMatch(window, @"distance\s*<\s*maxDistance") ||
                    Regex.IsMatch(window, @"distance\s*<\s*[^;{}]*Panel2MinSize") ||
                    Regex.IsMatch(window, @"maxDistance\s*>\s*distance") ||
                    Regex.IsMatch(window, @"[^;{}]*Panel2MinSize[^;{}]*>\s*distance");

                Assert.False(
                    dead,
                    $"{relativePath}: a restore site gates its SplitterDistance assignment on a " +
                    "'distance < max' comparison that the clamp above has already forced to be " +
                    "false whenever it fires. That discards the restore for every saved ratio too " +
                    "large to fit at the current height. Test '> Panel1MinSize' only, and if the " +
                    "site latches, gate the latch on whether the value was clamped. Offending " +
                    $"region:\n{window.Trim()}");
            }
        }

        /// <summary>
        /// The one-shot restore latch must not be set by a restore that only fitted after clamping.
        /// </summary>
        /// <remarks>
        /// This is the other half, and it is the half a comparison-only test misses. `OnHudSplitterSizeChanged`
        /// is a retry loop: it runs on every SizeChanged until the latch closes it. Latching on a
        /// clamped restore ends the retries at a transient docking height, so the correct restore at
        /// the final height never happens and the position is wrong for the rest of the session —
        /// then persisted, then broadcast to every other terminal.
        /// </remarks>
        [Fact]
        public void The_restore_latch_is_not_set_by_a_clamped_fit()
        {
            string src = ReadStripped("Docking", "TerminalDocument.cs");

            int handler = src.IndexOf("private void OnHudSplitterSizeChanged", StringComparison.Ordinal);
            Assert.True(handler >= 0, "OnHudSplitterSizeChanged not found — it was renamed or removed.");

            string body = EnclosingBlockFrom(src, src.IndexOf('{', handler) + 1);

            // `>=`, not `>`. maxDistance sits SplitterWidth above the real WinForms ceiling, so a
            // distance equal to it is clamped in practice. With `>` the latch condition became a
            // strict superset of the original's and the extra at-the-ceiling case reproduced the
            // bug. One character, and no other assertion here can see it.
            Assert.True(
                Regex.IsMatch(body, @"bool\s+clamped\s*=\s*distance\s*>=\s*maxDistance\s*;"),
                "OnHudSplitterSizeChanged must capture `bool clamped = distance >= maxDistance;`. " +
                "A strict `>` treats the at-the-ceiling case as a fit and latches there, which pins " +
                "the HUD at Panel2MinSize for the session; dropping the capture entirely loses the " +
                "retry-at-a-larger-height behaviour altogether.");

            int guard = body.IndexOf("if (!clamped)", StringComparison.Ordinal);
            Assert.True(
                guard >= 0,
                "OnHudSplitterSizeChanged no longer guards on `if (!clamped)`. Without it the latch " +
                "closes on a restore measured at a transient docking height.");

            // Containment, NOT ordering. An earlier version of this test asserted only that the
            // latch appeared AFTER the guard, which stays green if the latch is moved BELOW a
            // surviving `if (!clamped) { }` block — i.e. the exact regression, unguarded again.
            string guarded = EnclosingBlockFrom(body, body.IndexOf('{', guard) + 1);

            foreach (string required in new[] { "_initialHudSplitApplied = true", "SplitterMoved += OnHudSplitterMoved" })
            {
                Assert.True(
                    guarded.Contains(required, StringComparison.Ordinal),
                    $"'{required}' is not INSIDE the 'if (!clamped)' block. Latching or subscribing " +
                    "outside that guard means a clamped restore at a transient height ends the retry " +
                    "loop, and FixedPanel.Panel2 then holds the HUD at its minimum for the session.");

                int total = Regex.Matches(body, Regex.Escape(required)).Count;
                Assert.True(
                    total == 1,
                    $"'{required}' appears {total} times in OnHudSplitterSizeChanged; expected exactly " +
                    "one, inside the guard. A second unguarded occurrence re-introduces the defect " +
                    "while the containment check above still passes on the guarded one.");
            }
        }

        /// <summary>
        /// The agent-splitter sites must keep their bounds test — there is no clamp above them.
        /// </summary>
        /// <remarks>
        /// The counterpart to the first test, and the reason that one keys on the clamp rather than
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
