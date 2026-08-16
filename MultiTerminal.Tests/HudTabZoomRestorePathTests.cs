using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The HUD-tab zoom RESTORE path (task 0d72698a, item 4).
    /// <para>Restore was never the broken half — it already reached every tab. It was broken in a
    /// subtler way: it carried ONE value for all of them, which is what made saving per tab pointless.
    /// These tests pin that the global apply is gone and cannot come back.</para>
    /// <para>Assertions run over comment-stripped source, because the surrounding comments discuss the
    /// removed global API by name while explaining why it was removed.</para>
    /// </summary>
    public class HudTabZoomRestorePathTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var kept = noBlocks
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            return string.Join("\n", kept);
        }

        private static string ReadStripped(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return StripComments(File.ReadAllText(path));
        }

        private static int CountMatches(string haystack, string pattern) =>
            Regex.Matches(haystack, pattern).Count;

        /// <summary>
        /// Every TerminalDocument that gets constructed must have its saved zoom restored, or that
        /// terminal opens at the default size regardless of what the user stored.
        /// </summary>
        /// <remarks>
        /// ANCHORED ON CONSTRUCTION SITES, not on sibling preferences — see the long note on
        /// <c>HudTabZoomSavePathTests.Zoom_is_wired_at_every_terminal_document_construction_site</c>.
        /// The sibling anchor was green while `ApplyGridLayout`'s document had neither zoom NOR the
        /// siblings, because the anchor and the subject shared the same blind spot.
        /// </remarks>
        [Fact]
        public void Zoom_is_restored_at_every_terminal_document_construction_site()
        {
            string mainForm = ReadStripped("MainForm.cs");

            int constructions = CountMatches(mainForm, @"new TerminalDocument\(\)");
            int restored = CountMatches(mainForm, @"ApplySavedHudTabZooms\(doc\)");

            Assert.True(constructions > 0, "Found no `new TerminalDocument()` sites — the anchor is broken, not the code.");
            Assert.True(
                restored == constructions,
                $"MainForm constructs {constructions} TerminalDocument(s) but restores saved zoom at only " +
                $"{restored} site(s). That terminal opens every HUD tab at the default size no matter what " +
                "was saved — the original complaint, surviving in one code path.");
        }

        /// <summary>
        /// THE ONE THAT MATTERS FOR OPTION B. A single call that applies one factor to every tab is how
        /// global-zoom behaviour would quietly return, undoing the Owner's choice while every test about
        /// storage still passed.
        /// </summary>
        [Fact]
        public void No_api_remains_that_applies_one_zoom_to_every_tab()
        {
            string container = ReadStripped("Controls", "HudTabContainer", "HudTabContainer.cs");
            string doc = ReadStripped("Docking", "TerminalDocument.cs");
            string mainForm = ReadStripped("MainForm.cs");

            Assert.DoesNotContain("public void SetZoomFactor(double", container);
            Assert.DoesNotContain("ApplyTaskHudZoom", doc);
            Assert.DoesNotContain("ApplyTaskHudZoom", mainForm);
        }

        /// <summary>
        /// Restore must read the per-tab getter. Reading the global one would apply the same number to
        /// every tab even though each now has its own key — the old behaviour wearing the new API.
        /// </summary>
        [Fact]
        public void Restore_reads_the_per_tab_setting()
        {
            string mainForm = ReadStripped("MainForm.cs");

            Assert.Contains("GetHudTabZoom(key)", mainForm);
            Assert.DoesNotContain("GetTaskHudZoom()", mainForm);
        }

        /// <summary>
        /// The browser bucket is restored even with no browser tab open. Applying a key with no matching
        /// tab shows nothing, but records the value, so a browser tab opened later adopts the remembered
        /// zoom rather than starting at 1.0.
        /// </summary>
        [Fact]
        public void The_browser_bucket_is_restored_even_when_no_browser_tab_is_open()
        {
            string mainForm = ReadStripped("MainForm.cs");

            Assert.Contains("HudTabContainer.BrowserZoomKey", mainForm);
        }
    }
}
