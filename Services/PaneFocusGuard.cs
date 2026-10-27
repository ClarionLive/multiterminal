using System;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Keeps keyboard focus in the pane the Owner is typing in (task 2be35d18). Owner report: "when I'm
    /// typing in a terminal in MT, suddenly I won't have focus and I find the cursor has moved to another
    /// terminal". Two things MT does on an agent's behalf took focus: a helper pane opened by
    /// <c>spawn_helper</c> (or resumed with its PM) was activated, and a subagent's live panel took focus
    /// when its page loaded. Each of those sites asks this rule first and leaves focus where it is.
    /// Nothing the Owner does (a click, Ctrl+Tab, a header button) goes through it, so it cannot undo the
    /// Owner's own switch. Pure.
    /// </summary>
    public static class PaneFocusGuard
    {
        /// <summary>How recent a keystroke in the focused pane must be for the Owner to count as typing there.</summary>
        public static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Whether a pane MT is opening for an agent (<paramref name="newPaneId"/>) must leave focus in
        /// <paramref name="focusedPaneId"/>: true when that is a different pane whose last keystroke
        /// (<paramref name="typedAtUtc"/>) is within <see cref="TypingWindow"/>.
        /// </summary>
        public static bool ShouldKeepFocus(string focusedPaneId, DateTime? typedAtUtc, string newPaneId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(focusedPaneId) || typedAtUtc == null) return false;
            if (string.Equals(focusedPaneId, newPaneId, StringComparison.Ordinal)) return false;
            return nowUtc - typedAtUtc.Value <= TypingWindow;
        }
    }
}
