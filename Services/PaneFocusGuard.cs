using System;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Keeps keyboard focus in the pane the Owner is typing in (task 2be35d18). Owner report: "when I'm
    /// typing in a terminal in MT, suddenly I won't have focus and I find the cursor has moved to another
    /// terminal". Things MT does on an agent's behalf took focus: a helper pane opened by
    /// <c>spawn_helper</c> (or resumed with its PM) was activated, a new terminal focused itself when its
    /// deferred start ran, and a subagent's live panel or an agent's HUD browser tab could take focus when
    /// its page loaded. Each of those sites asks this rule and leaves focus where it is. Nothing the Owner
    /// does (a click, Ctrl+Tab, a header button) goes through it, so it cannot undo the Owner's own switch.
    /// Pure.
    /// </summary>
    public static class PaneFocusGuard
    {
        /// <summary>How recent a keystroke in the focused pane must be for the Owner to count as typing there.</summary>
        public static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Whether something MT is opening for an agent must leave focus in <paramref name="focusedPaneId"/>:
        /// true when the Owner is typing there, meaning its input line holds text not yet submitted
        /// (<paramref name="hasUnsentText"/>, however long ago the last key was) or its last keystroke
        /// (<paramref name="typedAtUtc"/>) is within <see cref="TypingWindow"/>. Never when the new thing
        /// is that same pane (<paramref name="newPaneId"/>); a panel or browser tab passes null.
        /// </summary>
        public static bool ShouldKeepFocus(string focusedPaneId, DateTime? typedAtUtc, bool hasUnsentText, string newPaneId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(focusedPaneId)) return false;
            if (string.Equals(focusedPaneId, newPaneId, StringComparison.Ordinal)) return false;
            if (hasUnsentText) return true;
            return typedAtUtc.HasValue && nowUtc - typedAtUtc.Value <= TypingWindow;
        }
    }
}
