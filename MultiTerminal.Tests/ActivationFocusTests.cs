using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// When activating a MultiTerminal window puts focus back in the bordered pane (task 76a9d71a).
    /// </summary>
    public class ActivationFocusTests
    {
        [Theory]
        [InlineData(ActivationFocusState.Nothing)]
        [InlineData(ActivationFocusState.Terminal)]
        public void AltTab_back_restores_the_bordered_pane(ActivationFocusState focused)
        {
            Assert.True(ActivationFocus.ShouldRestore(mouseButtonDown: false, hasBorderedTerminal: true, focused));
        }

        [Theory]
        [InlineData(ActivationFocusState.Nothing)]
        [InlineData(ActivationFocusState.Terminal)]
        [InlineData(ActivationFocusState.OtherControl)]
        public void A_click_activation_leaves_focus_where_the_click_put_it(ActivationFocusState focused)
        {
            Assert.False(ActivationFocus.ShouldRestore(mouseButtonDown: true, hasBorderedTerminal: true, focused));
        }

        [Theory]
        [InlineData(ActivationFocusState.Nothing)]
        [InlineData(ActivationFocusState.Terminal)]
        public void A_window_without_the_bordered_pane_is_left_alone(ActivationFocusState focused)
        {
            Assert.False(ActivationFocus.ShouldRestore(mouseButtonDown: false, hasBorderedTerminal: false, focused));
        }

        [Fact]
        public void Focus_in_another_control_is_kept()
        {
            Assert.False(ActivationFocus.ShouldRestore(mouseButtonDown: false, hasBorderedTerminal: true, ActivationFocusState.OtherControl));
        }
    }
}
