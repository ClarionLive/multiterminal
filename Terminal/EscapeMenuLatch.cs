namespace MultiTerminal.Terminal
{
    /// <summary>
    /// Decides, host-side, whether an Esc key event is taken away from the terminal page because
    /// the terminal menu is open or has just been dismissed by that same press (task 11edbec4,
    /// GH #24). Pure: WebViewTerminalRenderer feeds it WebView2 key and focus events.
    ///
    /// <para>The press that dismisses the menu stays "held" until its keyup, so its auto-repeats
    /// are taken too, however late they arrive (blocked UI thread, remote session, slow repeat
    /// settings). The latch ends only on Esc keyup, focus loss, or a different key. There is
    /// deliberately NO time-based expiry: one expired inside a held press, letting a late repeat
    /// reach the app as an interrupt, which is the defect this exists to prevent.</para>
    ///
    /// <para>Accepted residual: WinForms <c>KeyEventArgs</c> carries no repeat flag, so if the
    /// dismissing keyup is lost while focus stays, the NEXT Esc press is taken once and its own
    /// keyup clears the latch. One Esc eaten, never one leaked.</para>
    /// </summary>
    internal sealed class EscapeMenuLatch
    {
        private bool _held;

        /// <summary>True while the dismissing Esc press is still considered held.</summary>
        public bool IsHeld => _held;

        /// <summary>
        /// A KeyDown. Returns whether the event is taken from the page; <paramref name="dismiss"/>
        /// is true when this press should close the menu.
        /// </summary>
        public bool OnKeyDown(bool isEscape, bool menuOpen, out bool dismiss)
        {
            dismiss = false;
            if (!isEscape)
            {
                _held = false; // another key: the dismissing press is over
                return false;
            }

            if (menuOpen)
            {
                _held = true;
                dismiss = true;
                return true;
            }

            return _held;
        }

        /// <summary>A KeyUp. Returns whether the event is taken from the page.</summary>
        public bool OnKeyUp(bool isEscape)
        {
            if (!isEscape || !_held) return false;
            _held = false;
            return true;
        }

        /// <summary>Focus left the WebView: the dismissing press's keyup will land elsewhere.</summary>
        public void OnLostFocus() => _held = false;
    }
}
