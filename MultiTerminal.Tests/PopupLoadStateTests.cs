using MultiTerminal.DashboardHeader;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// The popup's retry rules (task 4cac608c, pipeline Run 2 verifier finding: a failed page load or
    /// a ready timeout used to leave every later click waiting 5 seconds and erroring until restart).
    /// <para>
    /// WHAT THIS PINS: given that a failure has been REPORTED to <see cref="PopupLoadState"/>
    /// (InitFailed or LoadFailed), the next show takes the right step. WHAT IT DOES NOT: that
    /// HeaderPopupForm reports each real failure. The wiring from the ready timer, NavigationCompleted
    /// and ProcessFailed to LoadFailed is read-checked only; it needs a live WebView2.
    /// </para>
    /// <para>
    /// Falsified by making LoadFailed forget the failure (the Run 2 bug): the failed-load,
    /// retried-once and both-kinds-recover facts go red. A_page_that_fails_after_it_was_ready stays green under that bug,
    /// because an already-ready page is no longer loading; it guards the reload, not that defect.
    /// </para>
    /// </summary>
    public class PopupLoadStateTests
    {
        private static PopupLoadState Started()
        {
            var s = new PopupLoadState();
            Assert.True(s.ShouldWarmUp());
            s.InitSucceeded();
            return s;
        }

        [Fact]
        public void A_ready_page_posts_at_once()
        {
            var s = Started();
            s.Ready();
            Assert.Equal(PopupLoadAction.Post, s.NextForShow());
        }

        [Fact]
        public void A_click_during_warm_up_waits_and_never_starts_a_second_init()
        {
            var s = new PopupLoadState();
            Assert.True(s.ShouldWarmUp());
            Assert.Equal(PopupLoadAction.Wait, s.NextForShow());
            Assert.False(s.ShouldWarmUp());
        }

        [Fact]
        public void A_click_while_the_page_is_still_loading_waits()
        {
            Assert.Equal(PopupLoadAction.Wait, Started().NextForShow());
        }

        [Fact]
        public void After_a_failed_page_load_the_next_click_reloads_the_page()
        {
            var s = Started();
            s.LoadFailed();
            Assert.Equal(PopupLoadAction.Reload, s.NextForShow());
        }

        [Fact]
        public void A_load_failure_is_retried_once_not_repeatedly()
        {
            // The ready timeout reports itself as a load failure (HeaderPopupForm; not run here), so
            // this is its path too: one reload, and the next click waits for it.
            var s = Started();
            s.LoadFailed();
            Assert.Equal(PopupLoadAction.Reload, s.NextForShow());
            Assert.Equal(PopupLoadAction.Wait, s.NextForShow()); // that reload is now under way
        }

        [Fact]
        public void After_a_failed_start_the_next_click_starts_again()
        {
            var s = new PopupLoadState();
            Assert.True(s.ShouldWarmUp());
            s.InitFailed();
            Assert.Equal(PopupLoadAction.Init, s.NextForShow());
        }

        [Fact]
        public void A_page_that_fails_after_it_was_ready_is_reloaded()
        {
            var s = Started();
            s.Ready();
            s.LoadFailed(); // as reported for a renderer exit (the ProcessFailed wiring is not run here)
            Assert.False(s.PageReady);
            Assert.Equal(PopupLoadAction.Reload, s.NextForShow());
        }

        [Fact]
        public void Both_failure_kinds_recover_once_the_page_answers()
        {
            var s = new PopupLoadState();
            Assert.True(s.ShouldWarmUp());
            s.InitFailed();
            Assert.Equal(PopupLoadAction.Init, s.NextForShow());
            s.InitSucceeded();
            s.LoadFailed();
            Assert.Equal(PopupLoadAction.Reload, s.NextForShow());
            s.Ready();
            Assert.Equal(PopupLoadAction.Post, s.NextForShow());
        }
    }
}
