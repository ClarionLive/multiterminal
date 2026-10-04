using MultiTerminal.DashboardHeader;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// After any failure, the next click on M or Select Project tries again (task 4cac608c, pipeline
    /// Run 2 verifier finding: a failed page load or a ready timeout used to leave every later click
    /// waiting 5 seconds and erroring until restart). One fact per failure path.
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
        public void After_a_ready_timeout_the_next_click_reloads_the_page()
        {
            // The timeout is reported as a load failure; it must not leave the page "still loading".
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
        public void A_page_that_dies_after_it_was_ready_is_reloaded()
        {
            var s = Started();
            s.Ready();
            s.LoadFailed(); // e.g. the renderer process exited
            Assert.False(s.PageReady);
            Assert.Equal(PopupLoadAction.Reload, s.NextForShow());
        }

        [Fact]
        public void Every_failure_path_recovers_once_the_page_answers()
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
