using System;
using MultiTerminal.DashboardHeader;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The rule for a WebView2 call that throws on a popup's show path (task 4cac608c, pipeline Run 3
    /// adversary finding): it is reported, the page is marked failed and the popup is marked for
    /// rebuilding. WHAT THIS DOES NOT PIN: that HeaderPopupForm routes every call through the guard;
    /// that is read-checked, as faking a throwing WebView2 needs the real control.
    /// </summary>
    public class PopupWebViewGuardTests
    {
        private static PopupLoadState Ready()
        {
            var s = new PopupLoadState();
            Assert.True(s.ShouldWarmUp());
            s.InitSucceeded();
            s.Ready();
            return s;
        }

        [Fact]
        public void A_call_that_succeeds_reports_nothing()
        {
            var load = Ready();
            bool broken = false;
            string failure = null;

            bool ok = PopupWebViewGuard.Run(() => { }, "show the menu", load, () => broken = true, r => failure = r);

            Assert.True(ok);
            Assert.False(broken);
            Assert.Null(failure);
            Assert.Equal(PopupLoadAction.Post, load.NextForShow());
        }

        [Fact]
        public void A_call_that_throws_is_reported_and_leaves_nothing_saying_ready()
        {
            var load = Ready();
            bool broken = false;
            string failure = null;

            bool ok = PopupWebViewGuard.Run(
                () => throw new InvalidOperationException("CoreWebView2 is closed"),
                "show the menu", load, () => broken = true, r => failure = r);

            Assert.False(ok);
            Assert.True(broken, "the popup must be marked for rebuilding");
            Assert.False(load.PageReady, "the load state still says ready, so the next click would post into a dead page");
            Assert.Contains("show the menu", failure);
            Assert.Contains("CoreWebView2 is closed", failure);
        }

        [Theory]
        [InlineData(true, true, true)]    // clicked M or Select Project, still waiting: tell the user
        [InlineData(true, false, false)]  // flyout opened by hovering over Grid Layout: log only
        [InlineData(false, true, false)]  // nobody is waiting (e.g. the startup warm-up): log only
        [InlineData(false, false, false)]
        public void Only_a_failed_show_the_user_asked_for_is_reported(bool pending, bool userInitiated, bool reported)
        {
            Assert.Equal(reported, HeaderPopupForm.ShouldReportFailure(pending, userInitiated));
        }
    }
}
