namespace MultiTerminal.Services
{
    /// <summary>
    /// Whether the header closes the Home tab it created for a project action (New Project, Just
    /// Claude, PowerShell, Launch Project) once that action's dialog is answered.
    /// </summary>
    /// <remarks>
    /// The header runs those actions in a Home tab, and creates one when no open tab shows its start
    /// screen. Cancelling the dialog then left that new, empty pane behind. It is closed only when this
    /// call created it, nothing was started in it, it still shows its start screen, and it is not the
    /// last terminal pane (MT is never left with none). A tab that already existed is never closed.
    /// </remarks>
    internal static class HomeTabCleanup
    {
        internal static bool ShouldClose(
            bool createdForAction,
            bool isDisposed,
            bool startScreenVisible,
            bool terminalStarted,
            int terminalPaneCount)
            => createdForAction
               && !isDisposed
               && startScreenVisible
               && !terminalStarted
               && terminalPaneCount > 1;
    }
}
