using System;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 2be35d18: while the Owner is typing, only the Owner moves focus to another pane. The rule is
    /// pure; the wiring (the message filter, the click sources, the restore in OnActiveDocumentChanged)
    /// runs only live.
    /// </summary>
    public class PaneFocusGuardTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Another_pane_taking_focus_while_the_owner_types_is_undone()
        {
            Assert.True(PaneFocusGuard.ShouldRestore("B", "A", Now.AddMilliseconds(-200), null, Now));
        }

        [Fact]
        public void A_switch_the_owner_pointed_at_is_kept()
        {
            // A tab click or a click in pane B, just after typing in A.
            Assert.False(PaneFocusGuard.ShouldRestore("B", "A", Now.AddMilliseconds(-200), Now.AddMilliseconds(-50), Now));
            Assert.False(PaneFocusGuard.ShouldRestore("B", "A", Now.AddMilliseconds(-200), Now - PaneFocusGuard.IntentWindow, Now));
        }

        [Fact]
        public void An_old_click_does_not_excuse_a_switch()
        {
            Assert.True(PaneFocusGuard.ShouldRestore("B", "A", Now.AddMilliseconds(-200),
                Now - PaneFocusGuard.IntentWindow - TimeSpan.FromMilliseconds(1), Now));
        }

        [Fact]
        public void Nothing_is_undone_once_the_owner_has_stopped_typing()
        {
            Assert.False(PaneFocusGuard.ShouldRestore("B", "A", Now - PaneFocusGuard.TypingWindow - TimeSpan.FromMilliseconds(1), null, Now));
            Assert.True(PaneFocusGuard.ShouldRestore("B", "A", Now - PaneFocusGuard.TypingWindow, null, Now));
        }

        [Fact]
        public void The_pane_being_typed_in_becoming_active_is_never_undone()
        {
            Assert.False(PaneFocusGuard.ShouldRestore("A", "A", Now.AddMilliseconds(-100), null, Now));
        }

        [Fact]
        public void With_no_typing_known_nothing_is_undone()
        {
            Assert.False(PaneFocusGuard.ShouldRestore("B", null, Now.AddMilliseconds(-100), null, Now));
            Assert.False(PaneFocusGuard.ShouldRestore("B", "A", null, null, Now));
        }

        [Fact]
        public void A_noted_click_reads_back_as_a_recent_utc_time()
        {
            UserFocusIntent.Note();
            var at = UserFocusIntent.LastUtc;
            Assert.NotNull(at);
            Assert.Equal(DateTimeKind.Utc, at.Value.Kind);
            Assert.True((DateTime.UtcNow - at.Value).TotalSeconds < 5);
        }
    }
}
