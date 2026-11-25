using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// What activating a MultiTerminal window does with the keyboard focus (task 76a9d71a).
    /// </summary>
    public class ActivationFocusTests
    {
        [Fact]
        public void AltTab_back_with_nothing_focused_goes_to_the_bordered_pane()
        {
            Assert.Equal(ActivationFocusTarget.BorderedPane,
                ActivationFocus.Decide(mouseButtonDown: false, hasBorderedTerminal: true, ActivationFocusState.Nothing));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_focused_terminal_wins_over_the_border(bool hasBorderedTerminal)
        {
            // MT raising itself (the REST open's BringPaneToFront) focuses the target pane without the border
            // necessarily following; the restore must re-assert that pane, not go back to the bordered one.
            Assert.Equal(ActivationFocusTarget.FocusedPane,
                ActivationFocus.Decide(mouseButtonDown: false, hasBorderedTerminal, ActivationFocusState.Terminal));
        }

        [Theory]
        [InlineData(ActivationFocusState.Nothing)]
        [InlineData(ActivationFocusState.Terminal)]
        [InlineData(ActivationFocusState.OtherControl)]
        public void A_click_activation_leaves_focus_where_the_click_put_it(ActivationFocusState focused)
        {
            Assert.Equal(ActivationFocusTarget.None,
                ActivationFocus.Decide(mouseButtonDown: true, hasBorderedTerminal: true, focused));
        }

        [Fact]
        public void Nothing_focused_and_no_bordered_pane_in_the_window_is_left_alone()
        {
            Assert.Equal(ActivationFocusTarget.None,
                ActivationFocus.Decide(mouseButtonDown: false, hasBorderedTerminal: false, ActivationFocusState.Nothing));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Focus_in_another_control_is_kept(bool hasBorderedTerminal)
        {
            Assert.Equal(ActivationFocusTarget.None,
                ActivationFocus.Decide(mouseButtonDown: false, hasBorderedTerminal, ActivationFocusState.OtherControl));
        }
    }
}
