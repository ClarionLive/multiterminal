using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Whether the header closes the Home tab it created for a project action whose dialog was answered
    /// (task 266c6135). MainForm.RunOnHomeTab's use of the rule, and the pane actually closing, is wiring
    /// no test executes; it is checked live.
    /// </summary>
    public sealed class HomeTabCleanupTests
    {
        [Fact]
        public void A_tab_created_for_a_cancelled_action_is_closed()
            => Assert.True(HomeTabCleanup.ShouldClose(
                createdForAction: true, isDisposed: false, startScreenVisible: true, terminalStarted: false, terminalPaneCount: 3));

        [Fact]
        public void A_tab_that_already_existed_is_never_closed()
            => Assert.False(HomeTabCleanup.ShouldClose(
                createdForAction: false, isDisposed: false, startScreenVisible: true, terminalStarted: false, terminalPaneCount: 3));

        [Fact]
        public void A_tab_where_the_action_started_a_terminal_is_kept()
            => Assert.False(HomeTabCleanup.ShouldClose(
                createdForAction: true, isDisposed: false, startScreenVisible: false, terminalStarted: true, terminalPaneCount: 3));

        [Fact]
        public void A_tab_whose_terminal_started_is_kept_even_if_it_still_reports_its_start_screen()
            => Assert.False(HomeTabCleanup.ShouldClose(
                createdForAction: true, isDisposed: false, startScreenVisible: true, terminalStarted: true, terminalPaneCount: 3));

        [Fact]
        public void The_last_terminal_pane_is_never_closed()
            => Assert.False(HomeTabCleanup.ShouldClose(
                createdForAction: true, isDisposed: false, startScreenVisible: true, terminalStarted: false, terminalPaneCount: 1));

        [Fact]
        public void A_tab_already_disposed_is_left_alone()
            => Assert.False(HomeTabCleanup.ShouldClose(
                createdForAction: true, isDisposed: true, startScreenVisible: true, terminalStarted: false, terminalPaneCount: 3));
    }
}
