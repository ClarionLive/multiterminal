namespace MultiTerminal.Services
{
    /// <summary>What a window has in focus when it is activated, as far as the focus restore cares.</summary>
    public enum ActivationFocusState
    {
        /// <summary>Nothing in the window has focus.</summary>
        Nothing,

        /// <summary>A terminal (inside some pane) has focus, as far as WinForms knows.</summary>
        Terminal,

        /// <summary>Some other control has focus: a pane's agent panel or HUD, a panel's search box.</summary>
        OtherControl,
    }

    /// <summary>Which pane, if any, the focus restore gives focus to.</summary>
    public enum ActivationFocusTarget
    {
        /// <summary>Leave focus where it is.</summary>
        None,

        /// <summary>The pane with the green border.</summary>
        BorderedPane,

        /// <summary>The pane whose terminal WinForms says has focus: used only when the window has no bordered pane.</summary>
        FocusedPane,
    }

    /// <summary>
    /// What activating a MultiTerminal window (Alt+Tab, the taskbar, MT raising itself) does with the keyboard focus.
    /// <para>
    /// WinForms restores focus to the window's last active control on activation, but a terminal is a WebView2,
    /// and the page inside it (xterm.js's input) does not get focus back that way. So the restore puts focus in a
    /// terminal after the activation has finished.
    /// </para>
    /// <para>
    /// The green border wins. It follows every click into a terminal (<c>OnTerminalClicked</c>), while WinForms'
    /// idea of the focused control does not: a click inside a WebView2 page never reaches WinForms, so it still
    /// names whichever terminal MT last focused itself. Code that moves focus on purpose moves the border too
    /// (<c>BringPaneToFront</c>, for the REST open), so the restore follows it.
    /// </para>
    /// </summary>
    internal static class ActivationFocus
    {
        /// <param name="mouseButtonDown">A mouse button is down: the window was activated by a click, which puts
        /// focus where it lands.</param>
        /// <param name="hasBorderedTerminal">This window holds the pane with the green border (not on its Home screen).</param>
        /// <param name="focused">What WinForms says has focus in the window now.</param>
        public static ActivationFocusTarget Decide(bool mouseButtonDown, bool hasBorderedTerminal, ActivationFocusState focused)
        {
            if (mouseButtonDown || focused == ActivationFocusState.OtherControl)
                return ActivationFocusTarget.None;
            if (hasBorderedTerminal)
                return ActivationFocusTarget.BorderedPane;
            return focused == ActivationFocusState.Terminal ? ActivationFocusTarget.FocusedPane : ActivationFocusTarget.None;
        }
    }
}
