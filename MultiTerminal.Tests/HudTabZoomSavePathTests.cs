using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The HUD-tab zoom SAVE path (task 0d72698a, item 3).
    /// <para>The save path runs through WinForms and WebView2, so it cannot be driven from a unit test.
    /// What CAN be pinned is the thing that actually broke: the wiring. This whole ticket exists because
    /// a subscription was missing at every site but one, and nothing anywhere failed — so these follow
    /// the PlanGraphDoorwayTests precedent and assert the cross-file contract over source.</para>
    /// <para>DISCIPLINE: the implementation comments in MainForm and TerminalDocument discuss
    /// TaskHudZoom and SetTaskHudZoom while explaining WHY they were replaced, so a bare Contains() over
    /// raw source would be satisfied by prose and pass on deleted code. Every assertion runs over
    /// comment-stripped text.</para>
    /// </summary>
    public class HudTabZoomSavePathTests
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
        /// THE ONE THAT MATTERS. Every TerminalDocument that gets constructed must have zoom wired.
        /// </summary>
        /// <remarks>
        /// <para>ANCHORED ON `new TerminalDocument()`, DELIBERATELY — and this is the second attempt.
        /// The first version compared the zoom-wiring count against sibling persisted preferences
        /// (StatusBarHeightChanged / HudSplitRatioChanged / AgentSplitRatioChanged). It was GREEN while
        /// a real bug existed: `ApplyGridLayout` constructs a sixth TerminalDocument and wired NONE of
        /// them — so the very siblings being measured against were also absent at the one broken site,
        /// and all four counts agreed at 5. The pipeline's debugger gate found it; the test could not.</para>
        /// <para>The lesson is general: an anchor drawn from the same population as the thing being
        /// measured cannot detect the failure the test exists for. Construction sites are independent
        /// of what any site remembered to do, so they are the honest invariant.</para>
        /// </remarks>
        [Fact]
        public void Zoom_is_wired_at_every_terminal_document_construction_site()
        {
            string mainForm = ReadStripped("MainForm.cs");

            int constructions = CountMatches(mainForm, @"new TerminalDocument\(\)");
            int wired = CountMatches(mainForm, @"doc\.HudTabZoomChanged \+=");

            Assert.True(constructions > 0, "Found no `new TerminalDocument()` sites — the anchor is broken, not the code.");
            Assert.True(
                wired == constructions,
                $"MainForm constructs {constructions} TerminalDocument(s) but wires doc.HudTabZoomChanged at " +
                $"only {wired} site(s). A construction site that does not subscribe means that terminal " +
                "silently never persists tab zoom — the defect task 0d72698a fixed, recurring. This is " +
                "exactly how ApplyGridLayout was missed until the pipeline's debugger gate caught it.");
        }

        /// <summary>
        /// The per-tab handler must not write the global key. That key is the fallback for tabs never
        /// zoomed individually, so writing it from one tab's change would move every untouched tab too —
        /// Option A behaviour leaking back in through the save path, after the Owner chose Option B.
        /// </summary>
        [Fact]
        public void The_per_tab_handler_persists_per_tab_and_never_writes_the_global_key()
        {
            string mainForm = ReadStripped("MainForm.cs");

            Assert.Contains("SetHudTabZoom(", mainForm);
            Assert.DoesNotContain("SetTaskHudZoom(", mainForm);
        }

        /// <summary>
        /// The old save path subscribed to the Tasks renderer directly, which is why exactly one tab
        /// persisted. Its return would quietly reinstate the asymmetry alongside the new path.
        /// </summary>
        [Fact]
        public void TerminalDocument_no_longer_subscribes_only_the_tasks_renderer()
        {
            string doc = ReadStripped("Docking", "TerminalDocument.cs");

            Assert.DoesNotContain("taskHudRenderer.ZoomChanged", doc);
            Assert.Contains("_hudTabContainer.TabZoomChanged", doc);
        }

        /// <summary>
        /// Re-entrancy is guarded per key. A single process-wide bool would also swallow a genuine
        /// change to a DIFFERENT tab that arrived while another tab was propagating — a silent lost
        /// write, which is the same class of failure the ticket is about.
        /// </summary>
        [Fact]
        public void Reentrancy_is_guarded_per_key_not_by_one_global_flag()
        {
            string mainForm = ReadStripped("MainForm.cs");

            Assert.Contains("_hudZoomSyncInFlight", mainForm);
            Assert.DoesNotContain("_suppressHudZoomSync", mainForm);
        }

        /// <summary>
        /// Propagation to other terminals must be per tab. Calling the all-tabs apply here would mirror
        /// one tab's zoom onto every tab of every other terminal.
        /// </summary>
        [Fact]
        public void Cross_terminal_propagation_targets_the_same_tab_not_all_tabs()
        {
            string mainForm = ReadStripped("MainForm.cs");

            Assert.Contains("ApplyHudTabZoom(e.ZoomKey, e.Zoom)", mainForm);
        }
    }
}
