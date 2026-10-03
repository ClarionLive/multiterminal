using System.Threading;
using MultiTerminal.Terminal;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Covers <see cref="EscapeMenuLatch"/>: which Esc events the host takes from the terminal page
    /// around the terminal menu (task 11edbec4, GH #24). These pin the rules only. That
    /// WebViewTerminalRenderer feeds the latch real WebView2 key events, and that WebView2 honours
    /// <c>Handled</c>, is not covered here and has not been verified live.
    /// </summary>
    public class EscapeMenuLatchTests
    {
        [Fact]
        public void Esc_with_the_menu_open_is_taken_and_dismisses()
        {
            var latch = new EscapeMenuLatch();
            Assert.True(latch.OnKeyDown(isEscape: true, menuOpen: true, out bool dismiss));
            Assert.True(dismiss);
        }

        [Fact]
        public void Esc_with_no_menu_and_no_held_press_reaches_the_page()
        {
            var latch = new EscapeMenuLatch();
            Assert.False(latch.OnKeyDown(isEscape: true, menuOpen: false, out bool dismiss));
            Assert.False(dismiss);
            Assert.False(latch.OnKeyUp(isEscape: true));
        }

        /// <summary>
        /// THE DEFECT the Run 2 review found: a time-based expiry ended the latch inside a held
        /// press, and a late auto-repeat reached Claude as an interrupt. The wait is real (longer
        /// than the removed 1200 ms window) so any clock-based expiry put back into the class fails
        /// this, whatever clock it reads.
        /// </summary>
        [Fact]
        public void A_held_Esc_repeat_arriving_after_1200_ms_is_still_taken()
        {
            var latch = new EscapeMenuLatch();
            latch.OnKeyDown(isEscape: true, menuOpen: true, out _);

            Thread.Sleep(1300);

            Assert.True(latch.OnKeyDown(isEscape: true, menuOpen: false, out bool dismiss));
            Assert.False(dismiss);
        }

        [Fact]
        public void The_dismissing_keyup_is_taken_and_ends_the_latch()
        {
            var latch = new EscapeMenuLatch();
            latch.OnKeyDown(isEscape: true, menuOpen: true, out _);

            Assert.True(latch.OnKeyUp(isEscape: true));
            Assert.False(latch.OnKeyDown(isEscape: true, menuOpen: false, out _));
        }

        [Fact]
        public void Focus_loss_ends_the_latch()
        {
            var latch = new EscapeMenuLatch();
            latch.OnKeyDown(isEscape: true, menuOpen: true, out _);

            latch.OnLostFocus();

            Assert.False(latch.OnKeyDown(isEscape: true, menuOpen: false, out _));
        }

        [Fact]
        public void A_different_key_is_never_taken_and_ends_the_latch()
        {
            var latch = new EscapeMenuLatch();
            latch.OnKeyDown(isEscape: true, menuOpen: true, out _);

            Assert.False(latch.OnKeyDown(isEscape: false, menuOpen: false, out _));
            Assert.False(latch.OnKeyDown(isEscape: true, menuOpen: false, out _));
        }

        /// <summary>
        /// The accepted residual, pinned so it is a decision rather than an accident: with the
        /// dismissing keyup lost and focus kept, the next press is taken once, and its own keyup
        /// heals the latch. One Esc eaten, never one leaked.
        /// </summary>
        [Fact]
        public void A_lost_keyup_costs_exactly_one_later_press()
        {
            var latch = new EscapeMenuLatch();
            latch.OnKeyDown(isEscape: true, menuOpen: true, out _);
            // keyup lost

            Assert.True(latch.OnKeyDown(isEscape: true, menuOpen: false, out _));
            Assert.True(latch.OnKeyUp(isEscape: true));

            Assert.False(latch.OnKeyDown(isEscape: true, menuOpen: false, out _));
        }
    }
}
