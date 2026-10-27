using System;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Keeps keyboard focus on the pane the Owner is typing in (task 2be35d18). Owner report: "when I'm
    /// typing in a terminal in MT, suddenly I won't have focus and I find the cursor has moved to another
    /// terminal". Panes and panels that MT creates for agents (a subagent's live panel, a spawned helper
    /// pane) take focus when they appear; that activation reaches <c>MainForm.OnActiveDocumentChanged</c>,
    /// which moves the cursor into the new pane mid-word. The rule: while the Owner is typing, only the
    /// Owner moves focus to another pane. Pure.
    /// </summary>
    public static class PaneFocusGuard
    {
        /// <summary>
        /// How recent a keystroke must be for its pane to count as the one being typed in. Short on
        /// purpose: a click inside another pane's web content (its HUD, an agent panel) is invisible to
        /// MT's message filter, so a longer window would undo a deliberate click made just after typing.
        /// A steal that happens while the Owner is typing falls well inside one second.
        /// </summary>
        public static readonly TimeSpan TypingWindow = TimeSpan.FromSeconds(1);

        /// <summary>How recent the Owner's own pointing (a click on a tab, a pane, a card) must be to explain a switch.</summary>
        public static readonly TimeSpan IntentWindow = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Whether an activation of <paramref name="activatedPaneId"/> should be undone, giving focus
        /// back to <paramref name="typingPaneId"/>. Only when a DIFFERENT pane became active within
        /// <see cref="TypingWindow"/> of a keystroke in the typing pane, and the Owner did nothing in the
        /// last <see cref="IntentWindow"/> that would explain the switch.
        /// </summary>
        public static bool ShouldRestore(string activatedPaneId, string typingPaneId, DateTime? typedAtUtc, DateTime? intentAtUtc, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(typingPaneId) || typedAtUtc == null) return false;
            if (string.Equals(activatedPaneId, typingPaneId, StringComparison.Ordinal)) return false;
            if (nowUtc - typedAtUtc.Value > TypingWindow) return false;
            if (intentAtUtc.HasValue && nowUtc - intentAtUtc.Value <= IntentWindow) return false;
            return true;
        }
    }

    /// <summary>
    /// When the Owner last pointed at something in MT: a mouse button on one of MT's own windows, a
    /// click inside a terminal, or a click in a page MT acts on by switching panes (an Attention card, a
    /// start-screen launch). Process-wide and thread-safe; read by <see cref="PaneFocusGuard"/>.
    /// </summary>
    public static class UserFocusIntent
    {
        private static long _ticks;

        public static void Note() => Interlocked.Exchange(ref _ticks, DateTime.UtcNow.Ticks);

        public static DateTime? LastUtc
        {
            get
            {
                long t = Interlocked.Read(ref _ticks);
                return t == 0 ? (DateTime?)null : new DateTime(t, DateTimeKind.Utc);
            }
        }
    }
}
