using System;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 2be35d18: a pane or panel MT opens for an agent leaves focus in the pane the Owner is typing
    /// in. The rule is pure; the two call sites (AddNewTerminal for spawned and resumed helpers, the
    /// agent panel's first page load) run only live.
    /// </summary>
    public class PaneFocusGuardTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_new_pane_leaves_focus_where_the_owner_is_typing()
        {
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", Now.AddMilliseconds(-200), "B", Now));
        }

        [Fact]
        public void The_window_ends_exactly_at_TypingWindow()
        {
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", Now - PaneFocusGuard.TypingWindow, "B", Now));
            Assert.False(PaneFocusGuard.ShouldKeepFocus("A", Now - PaneFocusGuard.TypingWindow - TimeSpan.FromMilliseconds(1), "B", Now));
        }

        [Fact]
        public void A_new_pane_takes_focus_when_nobody_has_typed()
        {
            Assert.False(PaneFocusGuard.ShouldKeepFocus("A", null, "B", Now));
            Assert.False(PaneFocusGuard.ShouldKeepFocus(null, Now.AddMilliseconds(-100), "B", Now));
        }

        [Fact]
        public void The_typing_pane_itself_is_never_held_against_itself()
        {
            Assert.False(PaneFocusGuard.ShouldKeepFocus("A", Now.AddMilliseconds(-100), "A", Now));
        }

        [Fact]
        public void A_panel_is_no_pane_so_typing_alone_decides()
        {
            // The agent panel's load passes no pane id: focus in the typing pane's own panel is still a steal.
            Assert.True(PaneFocusGuard.ShouldKeepFocus("A", Now.AddMilliseconds(-100), null, Now));
        }
    }
}
