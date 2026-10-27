using System;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 2be35d18: a pane, panel or browser tab MT opens for an agent leaves focus in the pane the
    /// Owner is typing in. The rule is pure; the call sites (AddNewTerminal for spawned and resumed
    /// helpers, TerminalControl's start, the agent panel's and browser tab's page loads) run only live.
    /// </summary>
    public class PaneFocusGuardTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_new_pane_leaves_focus_where_the_owner_is_typing()
        {
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", Now.AddMilliseconds(-200), false, "B", Now));
        }

        [Fact]
        public void The_window_ends_exactly_at_TypingWindow()
        {
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", Now - PaneFocusGuard.TypingWindow, false, "B", Now));
            Assert.False(PaneFocusGuard.ShouldKeepFocus("A", Now - PaneFocusGuard.TypingWindow - TimeSpan.FromMilliseconds(1), false, "B", Now));
        }

        [Fact]
        public void Unsent_text_counts_as_typing_however_long_the_pause()
        {
            // The Owner stopped mid-prompt to read or think: the last key was long ago, the line is not sent.
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", Now.AddSeconds(-30), true, "B", Now));
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", null, true, "B", Now));
        }

        [Fact]
        public void A_new_pane_takes_focus_when_nobody_is_typing()
        {
            Assert.False(PaneFocusGuard.ShouldKeepFocus("A", null, false, "B", Now));
            Assert.False(PaneFocusGuard.ShouldKeepFocus("A", Now.AddSeconds(-30), false, "B", Now));
            Assert.False(PaneFocusGuard.ShouldKeepFocus(null, Now.AddMilliseconds(-100), true, "B", Now));
        }

        [Fact]
        public void The_typing_pane_itself_is_never_held_against_itself()
        {
            Assert.False(PaneFocusGuard.ShouldKeepFocus("A", Now.AddMilliseconds(-100), true, "A", Now));
        }

        [Fact]
        public void A_panel_is_no_pane_so_typing_alone_decides()
        {
            // A panel or browser tab passes no pane id: focus in the typing pane's own panel is still a steal.
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", Now.AddMilliseconds(-100), false, null, Now));
        }
    }
}
