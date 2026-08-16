using System;
using System.IO;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Per-HUD-tab zoom persistence (task 0d72698a, item 0).
    /// <para>The defect being fixed is "zoom holds for the session and is lost on restart", so these
    /// tests model a restart literally: write with one <see cref="SettingsService"/>, then construct a
    /// SECOND one over the same folder and read. An assertion made against the same in-memory instance
    /// would pass even if nothing were ever written to disk, which is precisely the bug.</para>
    /// </summary>
    public sealed class HudTabZoomSettingsTests : IDisposable
    {
        private readonly string _dir;

        public HudTabZoomSettingsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"mt_settings_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // A leaked temp folder is not worth failing a test run over.
            }
        }

        private SettingsService NewService() => new SettingsService(_dir);

        /// <summary>A tab's zoom survives the process ending — the actual user complaint.</summary>
        [Fact]
        public void Tab_zoom_survives_a_restart()
        {
            NewService().SetHudTabZoom("__graph__", 0.8);

            Assert.Equal(0.8, NewService().GetHudTabZoom("__graph__"), 3);
        }

        /// <summary>
        /// The half that distinguishes Option B from Option A, and the Owner's actual request:
        /// zooming the Plan tab must not move any other tab.
        /// </summary>
        [Fact]
        public void Zooming_one_tab_leaves_its_siblings_alone()
        {
            var write = NewService();
            write.SetTaskHudZoom(1.0);
            write.SetHudTabZoom("__graph__", 0.6);

            var read = NewService();
            Assert.Equal(0.6, read.GetHudTabZoom("__graph__"), 3);
            Assert.Equal(1.0, read.GetHudTabZoom("__notes__"), 3);
            Assert.Equal(1.0, read.GetHudTabZoom("__git__"), 3);
        }

        /// <summary>
        /// THE UPGRADE PATH. Before this feature a single global TaskHudZoom was applied to every tab.
        /// If per-tab keys started empty, the first launch after the upgrade would reset every tab to
        /// 1.0 — performing the exact bug this ticket fixes, one last time, as its own rollout.
        /// </summary>
        [Fact]
        public void An_untouched_tab_inherits_the_pre_upgrade_global_zoom()
        {
            // Simulates an existing install: the old global key is set, no per-tab keys exist.
            NewService().SetTaskHudZoom(0.8);

            var afterUpgrade = NewService();
            Assert.Equal(0.8, afterUpgrade.GetHudTabZoom("__graph__"), 3);
            Assert.Equal(0.8, afterUpgrade.GetHudTabZoom("__notes__"), 3);
            Assert.Equal(0.8, afterUpgrade.GetHudTabZoom("__sessions__"), 3);
        }

        /// <summary>
        /// Once a tab is zoomed individually it stops following the global value, so the inheritance
        /// above is a one-way default and not a permanent tether.
        /// </summary>
        [Fact]
        public void An_individually_zoomed_tab_stops_following_the_global_value()
        {
            var s = NewService();
            s.SetTaskHudZoom(0.8);
            s.SetHudTabZoom("__graph__", 1.5);
            s.SetTaskHudZoom(0.9);

            var read = NewService();
            Assert.Equal(1.5, read.GetHudTabZoom("__graph__"), 3);   // its own value wins
            Assert.Equal(0.9, read.GetHudTabZoom("__notes__"), 3);   // still inheriting
        }

        [Theory]
        [InlineData(99.0, 5.0)]
        [InlineData(0.001, 0.25)]
        public void Zoom_is_clamped_to_the_panel_range(double written, double expected)
        {
            NewService().SetHudTabZoom("__graph__", written);

            Assert.Equal(expected, NewService().GetHudTabZoom("__graph__"), 3);
        }

        /// <summary>
        /// The settings file is line-oriented "key=value" split on the FIRST '='. A tab id carrying '='
        /// or a line break would round-trip as a different key, or truncate a neighbouring setting, so
        /// the write is refused. Losing one tab's zoom beats corrupting every setting in the file.
        /// </summary>
        [Theory]
        [InlineData("bad=id")]
        [InlineData("bad\nid")]
        [InlineData("bad\rid")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void An_unstorable_tab_id_is_refused_and_cannot_corrupt_neighbouring_settings(string tabId)
        {
            var s = NewService();
            s.SetTaskHudZoom(0.7);
            s.SetHudTabZoom("__graph__", 1.2);

            s.SetHudTabZoom(tabId, 3.0);

            var read = NewService();
            Assert.Equal(1.2, read.GetHudTabZoom("__graph__"), 3);  // neighbour intact
            Assert.Equal(0.7, read.GetTaskHudZoom(), 3);            // global intact
            Assert.Equal(0.7, read.GetHudTabZoom(tabId), 3);        // refused id reads as the default
        }

        /// <summary>
        /// REGRESSION — pipeline Run 1, debugger gate, LOW. A non-finite value must never reach a tab.
        /// </summary>
        /// <remarks>
        /// `double.TryParse` accepts "NaN" and "Infinity", and `Math.Max`/`Math.Min` PROPAGATE NaN
        /// instead of clamping — so the [0.25, 5.0] guard was a no-op for those inputs and NaN would be
        /// handed to WebView2.ZoomFactor. Worse, NaN defeats every downstream equality check
        /// (Math.Abs(NaN - x) &lt; eps is never true), latching the container's echo guard permanently
        /// open. Only reachable by hand-editing settings.txt, but the fix is one comparison.
        /// </remarks>
        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        [InlineData("not-a-number")]
        public void A_non_finite_or_unparseable_stored_value_falls_back_instead_of_reaching_a_tab(string stored)
        {
            var seed = NewService();
            seed.SetTaskHudZoom(0.8);
            seed.Set("HudTabZoom:__graph__", stored);

            double actual = NewService().GetHudTabZoom("__graph__");

            Assert.False(double.IsNaN(actual));
            Assert.False(double.IsInfinity(actual));
            Assert.Equal(0.8, actual, 3);
        }

        /// <summary>Writing a non-finite zoom is refused rather than clamped into the file.</summary>
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void A_non_finite_zoom_is_never_written(double bad)
        {
            var s = NewService();
            s.SetHudTabZoom("__graph__", 1.2);

            s.SetHudTabZoom("__graph__", bad);

            Assert.Equal(1.2, NewService().GetHudTabZoom("__graph__"), 3);
        }

        /// <summary>
        /// Dynamic browser tabs deliberately share ONE bucket rather than getting a key each: they are
        /// created with arbitrary runtime ids and closed for good, so per-tab keys would grow the
        /// settings file without bound for tabs that will never reopen.
        /// </summary>
        [Fact]
        public void Browser_tabs_share_a_single_bucket_key()
        {
            NewService().SetHudTabZoom("__browser__", 1.3);

            Assert.Equal(1.3, NewService().GetHudTabZoom("__browser__"), 3);
        }
    }
}
