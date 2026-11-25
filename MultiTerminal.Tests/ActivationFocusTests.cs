using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// What activating a MultiTerminal window does with the keyboard focus (task 76a9d71a).
    /// </summary>
    public class ActivationFocusTests
    {
        [Theory]
        [InlineData(ActivationFocusState.Nothing)]
        [InlineData(ActivationFocusState.Terminal)]
        public void AltTab_back_goes_to_the_bordered_pane(ActivationFocusState focused)
        {
            // Terminal included: WinForms' focused terminal is stale after a click inside a WebView2 page,
            // so it must not beat the border, which follows clicks. (Owner's live report after the first fix.)
            Assert.Equal(ActivationFocusTarget.BorderedPane,
                ActivationFocus.Decide(mouseButtonDown: false, hasBorderedTerminal: true, focused));
        }

        [Fact]
        public void Without_a_bordered_pane_a_focused_terminal_gets_focus_back()
        {
            Assert.Equal(ActivationFocusTarget.FocusedPane,
                ActivationFocus.Decide(mouseButtonDown: false, hasBorderedTerminal: false, ActivationFocusState.Terminal));
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
