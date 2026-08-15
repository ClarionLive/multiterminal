using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 60665c6c, item 9 — the Plan tab scrolled sideways on the Owner's first deployed build.
    ///
    /// The graph was not too big; the container was told to be too narrow. Two CSS rules combined:
    /// <c>#tiers { min-width: min-content }</c> sized the column to whatever its widest row demanded,
    /// and <c>.tier { flex-wrap: nowrap }</c> forbade that row from ever breaking. A ticket whose
    /// items declare no dependencies puts EVERY item on tier 0 — the state of every ticket in the
    /// database today — so nine nodes at <c>max-width: 11rem</c> asked for ~1660px inside a ~1190px
    /// canvas and the whole diagram went off the right edge.
    ///
    /// Wrapping is safe here for a reason worth recording: ChecklistGraphBuilder.AssignTiers puts
    /// external nodes on their own tiers (upstream at 0, downstream at maxItemTier + 1) and a
    /// dependency edge always spans tiers, so there are NO intra-tier edges. Wrapping a tier can
    /// therefore never split the two ends of an edge across two lines of the same row.
    ///
    /// The third assertion is the one that carries meaning rather than mechanics. A wrapped line is
    /// a CONTINUATION of its tier, and this feature's whole vocabulary is "same row means genuinely
    /// parallel". If the row-gap ever grows to rival the inter-tier gap, a wrapped tier reads as two
    /// tiers and the diagram starts asserting a dependency nobody declared — the same anti-invention
    /// failure the builder's zero-edge guarantee exists to prevent, reintroduced as a spacing bug.
    ///
    /// NOTE ON DISCIPLINE (the item-④ lesson from ticket 46e61d12): these assertions run against
    /// COMMENT-STRIPPED CSS. The fix's own explanatory comment contains the literal text
    /// "flex-wrap: nowrap" while describing what was removed, so a bare Contains() over the raw file
    /// would be satisfied by the comment and pass on reverted code. Stripping comments first is what
    /// makes these tests falsifiable rather than decorative.
    /// </summary>
    public class HudGraphLayoutTests
    {
        /// <summary>
        /// Resolves the repo root from THIS FILE's compile-time path rather than the working
        /// directory or assembly location, so the test is unaffected by where the runner is invoked
        /// from and works unchanged inside a git worktree.
        /// </summary>
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        /// <summary>Reads hud-graph.html with every /* ... */ comment removed. See the class remarks.</summary>
        private static string ReadStylesheetWithoutComments()
        {
            string path = Path.Combine(RepoRoot(), "Controls", "HudGraphPanel", "hud-graph.html");
            Assert.True(File.Exists(path), $"Could not locate hud-graph.html at '{path}'.");

            string src = File.ReadAllText(path);
            return Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        }

        /// <summary>
        /// Extracts the declaration body of a single CSS rule, so an assertion about
        /// <c>.tier</c> cannot be satisfied by an unrelated rule elsewhere in the file
        /// (<c>footer</c> also sets flex-wrap, for instance).
        /// </summary>
        private static string RuleBody(string css, string selector)
        {
            var match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{([^}]*)\}");
            Assert.True(match.Success, $"Could not find a '{selector} {{ ... }}' rule in hud-graph.html.");
            return match.Groups[1].Value;
        }

        private static double RemValue(string ruleBody, string property, string selector)
        {
            var match = Regex.Match(ruleBody, @"(?<![\w-])" + Regex.Escape(property) + @"\s*:\s*([0-9.]+)rem");
            Assert.True(
                match.Success,
                $"Expected '{selector}' to declare '{property}' in rem. Body was: {ruleBody.Trim()}");
            return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Tier_rows_wrap_instead_of_forcing_a_horizontal_scrollbar()
        {
            string body = RuleBody(ReadStylesheetWithoutComments(), ".tier");

            Assert.DoesNotContain("nowrap", body, StringComparison.Ordinal);
            Assert.Matches(@"flex-wrap\s*:\s*wrap", body);
        }

        [Fact]
        public void Tiers_container_does_not_widen_itself_to_its_widest_row()
        {
            string body = RuleBody(ReadStylesheetWithoutComments(), "#tiers");

            // min-width: min-content is what let the column outgrow the canvas. Any min-width
            // floor reintroduces the defect, so the assertion is against the property, not the
            // one value that happened to be there.
            Assert.DoesNotContain("min-width", body, StringComparison.Ordinal);
        }

        [Fact]
        public void Wrapped_line_stays_visually_subordinate_to_a_real_tier_break()
        {
            string css = ReadStylesheetWithoutComments();

            double tierGap = RemValue(RuleBody(css, "#tiers"), "gap", "#tiers");
            double wrapGap = RemValue(RuleBody(css, ".tier"), "row-gap", ".tier");

            Assert.True(
                wrapGap <= tierGap / 2,
                $"A wrapped line must read as a continuation of its tier, not as a new tier. " +
                $"row-gap ({wrapGap}rem) must be at most half the inter-tier gap ({tierGap}rem).");
        }
    }
}
