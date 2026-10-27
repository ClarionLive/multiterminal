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

        /// <summary>
        /// How much later than a terminal keystroke the system's last-input time may be and still be that
        /// same keystroke: the key-up, and the delay before xterm.js reports the key to MT.
        /// </summary>
        public static readonly TimeSpan SameInputSlack = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Whether the Owner did anything (a key, a click, a mouse move, anywhere) after
        /// <paramref name="sinceUtc"/>, plus <see cref="SameInputSlack"/>, judged from the system's
        /// last-input time (<paramref name="lastSystemInputUtc"/>). Unknown counts as acted, so MT then
        /// leaves focus alone: a site that moves focus for the Owner may do so only when nothing the
        /// Owner did since could have been a deliberate switch.
        /// </summary>
        public static bool OwnerActedSince(DateTime? lastSystemInputUtc, DateTime sinceUtc)
        {
            if (lastSystemInputUtc == null) return true;
            return lastSystemInputUtc.Value > sinceUtc + SameInputSlack;
        }
    }

    /// <summary>When the Owner last used the keyboard or mouse anywhere in the session (Win32 GetLastInputInfo).</summary>
    public static class OwnerInputClock
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>The last input time in UTC, or null when Windows does not say.</summary>
        public static DateTime? LastInputUtc()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return null;
            // Both are milliseconds since boot, wrapping at 2^32; unsigned subtraction handles the wrap.
            uint ageMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return DateTime.UtcNow - TimeSpan.FromMilliseconds(ageMs);
        }
    }
}
