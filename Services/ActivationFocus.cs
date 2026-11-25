namespace MultiTerminal.Services
{
    /// <summary>What a window has in focus when it is activated, as far as the focus restore cares.</summary>
    public enum ActivationFocusState
    {
        /// <summary>Nothing in the window has focus.</summary>
        Nothing,

        /// <summary>A terminal (inside some pane) has focus.</summary>
        Terminal,

        /// <summary>Some other control has focus: a pane's agent panel or HUD, a panel's search box.</summary>
        OtherControl,
    }

    /// <summary>Which pane, if any, the focus restore gives focus to.</summary>
    public enum ActivationFocusTarget
    {
        /// <summary>Leave focus where it is.</summary>
        None,

        /// <summary>The pane whose terminal already has focus: re-assert it, so xterm.js's input gets it too.</summary>
        FocusedPane,

        /// <summary>The pane with the green border.</summary>
        BorderedPane,
    }

    /// <summary>
    /// What activating a MultiTerminal window (Alt+Tab, the taskbar, MT raising itself) does with the keyboard focus.
    /// <para>
    /// WinForms restores focus to the window's last active control on activation, but a terminal is a WebView2,
    /// and the page inside it (xterm.js's input) does not get focus back that way. So the restore re-asserts focus
    /// on a terminal after the activation has finished.
    /// </para>
    /// <para>
    /// A terminal that already has focus wins over the green border. MT's own code can focus a pane while raising
    /// the window (the REST open's <c>BringPaneToFront</c>, for one) without the border following, so restoring the
    /// bordered pane there would undo that switch.
    /// </para>
    /// </summary>
    internal static class ActivationFocus
    {
        /// <param name="mouseButtonDown">A mouse button is down: the window was activated by a click, which puts
        /// focus where it lands.</param>
        /// <param name="hasBorderedTerminal">This window holds the pane with the green border (not on its Home screen).</param>
        /// <param name="focused">What has focus in the window now.</param>
        public static ActivationFocusTarget Decide(bool mouseButtonDown, bool hasBorderedTerminal, ActivationFocusState focused)
        {
            if (mouseButtonDown)
                return ActivationFocusTarget.None;

            switch (focused)
            {
                case ActivationFocusState.Terminal:
                    return ActivationFocusTarget.FocusedPane;
                case ActivationFocusState.Nothing:
                    return hasBorderedTerminal ? ActivationFocusTarget.BorderedPane : ActivationFocusTarget.None;
                default:
                    // Focus in a pane's agent panel or HUD, or a panel's own control, is where you left it: keep it.
                    return ActivationFocusTarget.None;
            }
        }
    }
}
