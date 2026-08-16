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
        /// Every site that restores the other persisted UI preferences must also restore zoom. A site
        /// that restores the splitters but not zoom leaves that terminal opening at the default size —
        /// the original complaint, surviving in one code path.
        /// </summary>
        [Fact]
        public void Zoom_is_restored_at_every_site_its_sibling_preferences_are_restored()
        {
            string mainForm = ReadStripped("MainForm.cs");

            // Restore sites only — matched by the "_settings.Get..." argument. The same Apply* methods
            // are ALSO called from the cross-terminal propagation handlers with a value passed in, and
            // counting those would compare restore against propagation. Zoom's propagation counterpart
            // lives in OnHudTabZoomChanged and is covered by HudTabZoomSavePathTests.
            int zoom = CountMatches(mainForm, @"ApplySavedHudTabZooms\(doc\)");
            int hudSplit = CountMatches(mainForm, @"doc\.ApplyHudSplitRatio\(_settings\.");
            int statusBar = CountMatches(mainForm, @"doc\.ApplyStatusBarHeight\(_settings\.");

            Assert.True(zoom > 0, "MainForm never restores saved HUD tab zoom.");
            Assert.True(
                zoom == hudSplit && zoom == statusBar,
                $"HUD zoom is restored at {zoom} site(s) but sibling preferences at " +
                $"HudSplitRatio={hudSplit}, StatusBarHeight={statusBar}. A restore site that applies the " +
                "splitters but not zoom means that terminal opens at the default size — the original bug.");
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
