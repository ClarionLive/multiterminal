namespace MultiTerminal.Services
{
    /// <summary>What a window has in focus when it is activated, as far as the focus restore cares.</summary>
    public enum ActivationFocusState
    {
        /// <summary>Nothing in the window has focus.</summary>
        Nothing,

        /// <summary>A terminal pane (or a control inside one) has focus.</summary>
        Terminal,

        /// <summary>Some other control has focus: a text box in a panel, a HUD, an agent card.</summary>
        OtherControl,
    }

    /// <summary>
    /// Whether activating a MultiTerminal window (Alt+Tab, the taskbar, another app closing) should put the
    /// keyboard focus back in the terminal that has the green border.
    /// <para>
    /// WinForms restores focus to the window's last active control on activation, but a terminal is a WebView2,
    /// and the page inside it (xterm.js's input) does not get focus back that way. The border still shows the
    /// pane as the one you are in, so typing goes nowhere until you click it.
    /// </para>
    /// </summary>
    internal static class ActivationFocus
    {
        /// <param name="mouseButtonDown">A mouse button is down: the window was activated by a click, which puts
        /// focus where it lands. Restoring the old pane would take it from the pane you clicked.</param>
        /// <param name="hasBorderedTerminal">This window holds the pane with the green border.</param>
        /// <param name="focused">What has focus in the window now.</param>
        public static bool ShouldRestore(bool mouseButtonDown, bool hasBorderedTerminal, ActivationFocusState focused)
        {
            if (mouseButtonDown || !hasBorderedTerminal)
                return false;

            // Focus in a panel's own control (a search box, say) is where you left it: keep it.
            return focused != ActivationFocusState.OtherControl;
        }
    }
}
