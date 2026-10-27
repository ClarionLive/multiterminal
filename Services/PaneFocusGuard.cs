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
        /// How much more recent than a terminal keystroke the system's last input may be and still be that
        /// same keystroke (its key-up, and xterm.js's delay in reporting the key to MT). Accepted gap: a
        /// click within this time of a keystroke reads as the keystroke.
        /// </summary>
        public static readonly TimeSpan SameInputSlack = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Whether the Owner did anything (a key, a click, a mouse move, anywhere) after the terminal
        /// keystroke that is <paramref name="keystrokeAge"/> old, judged from the age of the system's last
        /// input (<paramref name="lastInputAge"/>), allowing <see cref="SameInputSlack"/> for the keystroke's
        /// own key-up. Ages, not clock times, so a change of the wall clock cannot reorder them. Unknown
        /// counts as acted, so MT then leaves focus alone.
        /// </summary>
        public static bool OwnerActedAfterKeystroke(TimeSpan? lastInputAge, TimeSpan keystrokeAge)
        {
            if (lastInputAge == null) return true;
            return lastInputAge.Value < keystrokeAge - SameInputSlack;
        }

        /// <summary>
        /// Whether there was any input since <paramref name="baselineTick"/> was read: the system's
        /// last-input tick has changed. Exact, with no slack. Unknown (either value null) counts as acted.
        /// </summary>
        public static bool OwnerActedSinceTick(uint? lastInputTickNow, uint? baselineTick)
        {
            if (lastInputTickNow == null || baselineTick == null) return true;
            return lastInputTickNow.Value != baselineTick.Value;
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

        /// <summary>The tick count (milliseconds since boot, wrapping) of the last input, or null when Windows does not say.</summary>
        public static uint? LastInputTick()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? info.dwTime : (uint?)null;
        }

        /// <summary>How long ago the last input was, from the tick clock (unsigned subtraction handles the wrap), or null.</summary>
        public static TimeSpan? LastInputAge()
        {
            uint? tick = LastInputTick();
            if (tick == null) return null;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - tick.Value));
        }
    }
}