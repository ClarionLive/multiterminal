using MultiTerminal.Controls;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Key mapping and echo suppression for per-tab HUD zoom (task 0d72698a, item 2).
    /// <para>These are the two rules in the container that fail quietly rather than loudly: a wrong key
    /// writes one tab's zoom over another's, and a missed echo guard turns restoring a saved zoom into
    /// a fresh "the user zoomed" event on every launch.</para>
    /// </summary>
    public sealed class HudTabZoomTrackerTests
    {
        [Theory]
        [InlineData("__graph__")]
        [InlineData("__git__")]
        [InlineData("__tasks__")]
        [InlineData("__notes__")]
        public void A_permanent_tab_is_keyed_by_its_own_id(string tabId)
        {
            Assert.Equal(tabId, HudTabZoomTracker.KeyFor(isPermanent: true, tabId));
        }

        /// <summary>
        /// Every browser tab collapses onto one key. They are opened with arbitrary runtime ids and
        /// closed for good, so a key each would grow settings.txt without bound with entries for tabs
        /// that can never be reopened to use them.
        /// </summary>
        [Theory]
        [InlineData("browser-a1b2c3")]
        [InlineData("browser-99")]
        [InlineData("some-runtime-id")]
        public void Every_dynamic_browser_tab_shares_one_bucket(string tabId)
        {
            Assert.Equal("__browser__", HudTabZoomTracker.KeyFor(isPermanent: false, tabId));
            Assert.Equal(HudTabZoomTracker.BrowserZoomKey, HudTabZoomTracker.KeyFor(false, tabId));
        }

        [Fact]
        public void A_genuine_change_is_reported()
        {
            var t = new HudTabZoomTracker();

            Assert.True(t.ShouldReport("__graph__", 0.8));
        }

        /// <summary>
        /// THE ECHO GUARD. Applying a saved zoom makes the WebView2 raise ZoomFactorChanged, which comes
        /// back as "the user zoomed to 0.8". Reporting that would turn every launch into a write, and —
        /// once cross-terminal propagation is wired — a broadcast to every other open terminal.
        /// </summary>
        [Fact]
        public void An_echo_of_a_zoom_we_just_applied_is_not_reported()
        {
            var t = new HudTabZoomTracker();
            t.Record("__graph__", 0.8);

            Assert.False(t.ShouldReport("__graph__", 0.8));
        }

        /// <summary>
        /// The guard must not be a one-shot: after an echo is swallowed, a real user zoom still counts.
        /// A guard that latched would silently stop persisting zoom after the first restore.
        /// </summary>
        [Fact]
        public void A_real_change_after_an_echo_is_still_reported()
        {
            var t = new HudTabZoomTracker();
            t.Record("__graph__", 0.8);
            Assert.False(t.ShouldReport("__graph__", 0.8));

            Assert.True(t.ShouldReport("__graph__", 1.1));
            Assert.False(t.ShouldReport("__graph__", 1.1));   // and the new value becomes the baseline
        }

        /// <summary>
        /// WebView2 zoom factors are floating point and arrive slightly off what was set, so exact
        /// equality would let every restore through as a "change".
        /// </summary>
        [Fact]
        public void A_negligible_difference_counts_as_the_same_zoom()
        {
            var t = new HudTabZoomTracker();
            t.Record("__graph__", 0.8);

            Assert.False(t.ShouldReport("__graph__", 0.8001));
            Assert.True(t.ShouldReport("__graph__", 0.9));
        }

        /// <summary>
        /// Keys are independent: zooming Plan must not suppress or overwrite Git. This is the per-tab
        /// promise of Option B expressed at the tracker level.
        /// </summary>
        [Fact]
        public void Keys_do_not_interfere_with_each_other()
        {
            var t = new HudTabZoomTracker();
            t.Record("__graph__", 0.6);

            Assert.False(t.ShouldReport("__graph__", 0.6));
            Assert.True(t.ShouldReport("__git__", 0.6));      // same value, different tab: a real change

            Assert.True(t.TryGetKnown("__graph__", out var graph));
            Assert.Equal(0.6, graph, 3);
            Assert.True(t.TryGetKnown("__git__", out var git));
            Assert.Equal(0.6, git, 3);
        }

        /// <summary>
        /// REGRESSION — pipeline Run 1, debugger gate, MEDIUM. The echo guard is keyed by TAB, never by
        /// persistence key, because several browser tabs deliberately share one key.
        /// </summary>
        /// <remarks>
        /// The bug: with the guard keyed by the shared bucket, browser tabs B1 and B2 both start at 1.0.
        /// The user zooms B1 to 1.5, which is reported and recorded. B2 is still on screen at 1.0. The
        /// user then wheels B2 up through 1.1 and 1.25 — both reported — and stops at 1.5, the size they
        /// already chose once. That last step matched the bucket's remembered value and was dropped as an
        /// echo: B2 displayed 1.5 while settings held 1.25, and no other terminal ever saw 1.5.
        /// A lost write that leaves the screen disagreeing with what was saved.
        /// </remarks>
        [Fact]
        public void One_tabs_zoom_is_never_mistaken_for_a_siblings_echo()
        {
            var t = new HudTabZoomTracker();

            // Both browser tabs share the __browser__ PERSISTENCE key, but are distinct tabs.
            t.Record("browser-1", 1.0);
            t.Record("browser-2", 1.0);

            Assert.True(t.ShouldReport("browser-1", 1.5));   // user zooms the first tab

            // The user now zooms the SECOND tab to the same value. It is a genuine change for that tab.
            Assert.True(t.ShouldReport("browser-2", 1.5));
        }

        /// <summary>
        /// The same guarantee stated the other way round: a tab's own repeat is still an echo. Fixing
        /// the sibling bug must not disable echo suppression altogether.
        /// </summary>
        [Fact]
        public void A_tabs_own_repeat_is_still_suppressed_after_the_sibling_fix()
        {
            var t = new HudTabZoomTracker();

            t.Record("browser-1", 1.5);
            Assert.False(t.ShouldReport("browser-1", 1.5));
            Assert.True(t.ShouldReport("browser-2", 1.5));
            Assert.False(t.ShouldReport("browser-2", 1.5));
        }

        [Fact]
        public void An_unknown_key_reports_nothing_known()
        {
            var t = new HudTabZoomTracker();

            Assert.False(t.TryGetKnown("__never_seen__", out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_missing_key_is_ignored_rather_than_reported(string key)
        {
            var t = new HudTabZoomTracker();

            Assert.False(t.ShouldReport(key, 1.2));
            t.Record(key, 1.2);   // must not throw
        }
    }
}
