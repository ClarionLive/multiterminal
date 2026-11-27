using System;
using System.Text;

namespace MultiTerminal.Services
{
    /// <summary>
    /// What an agent that clears its own context leaves for the fresh session (ticket cc2e51ff). Owner report
    /// 2026-11-24 (Grace, ClarionClone PM): a brief such as "write your notes, clear_my_context, then claim card X"
    /// lost its second half every time, because <c>clear_my_context</c> only typed <c>/clear</c> and the fresh
    /// session started from the hook text alone. Now the tool takes a <c>continueWith</c> text; MT holds it for
    /// that pane (<see cref="Arm"/>) and adds it to the prompt it types after the clear (<see cref="Compose"/>).
    ///
    /// <para>One per pane, used once: the first post-clear prompt that passes the pane's dedupe takes it, and a
    /// second <see cref="Arm"/> replaces the first. It expires after <see cref="Expiry"/>, so a note whose
    /// <c>/clear</c> never landed cannot be attached to some later, unrelated clear. Not thread-safe by itself;
    /// <c>TerminalControl</c> calls it under its own clear-inject lock.</para>
    /// </summary>
    public sealed class ClearContinueNote
    {
        /// <summary>
        /// How long an armed note waits for its clear. The hook's request arrives about 4 s after the agent's
        /// <c>/clear</c> (MT log, 2026-11-24); a short window keeps a note whose clear never came from riding on
        /// a later, unrelated one (pipeline Run 1).
        /// </summary>
        public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(2);

        /// <summary>Longest note kept; the rest is cut. It is typed into the pane one character at a time.</summary>
        public const int MaxLength = 1500;

        private string _note;
        private DateTime _armedAtUtc;
        private DateTime? _clearTypedAtUtc;

        /// <summary>
        /// Holds <paramref name="text"/> (after <see cref="Sanitize"/>) for the pane's next clear. Blank text
        /// clears any note already held, so a clear without a note never gets an older one.
        /// </summary>
        public void Arm(string text, DateTime nowUtc)
        {
            _note = Sanitize(text);
            _armedAtUtc = nowUtc;
            _clearTypedAtUtc = null;
        }

        /// <summary>
        /// The note's <c>/clear</c> has been typed (pipeline Run 2). Until then no trigger may take the note:
        /// MT types into the pane's live process, so a trigger before the clear (a late hook from an earlier
        /// clear) would put the note into the OLD session.
        /// </summary>
        public void NoteClearTyped(DateTime nowUtc)
        {
            if (_note != null && _clearTypedAtUtc == null)
                _clearTypedAtUtc = nowUtc;
        }

        /// <summary>The held note, once; null when there is none or it has expired.</summary>
        public string TryTake(DateTime nowUtc)
        {
            string note = _note;
            if (note == null)
                return null;
            // Not typed yet: leave it for the trigger of its own clear (a stale earlier one must not take it).
            if (_clearTypedAtUtc == null || nowUtc < _clearTypedAtUtc.Value)
                return null;
            _note = null;
            if (nowUtc - _armedAtUtc > Expiry || nowUtc < _armedAtUtc)
                return null;
            return note;
        }

        /// <summary>
        /// The note as it can be typed into a terminal: one line (every control character, line breaks
        /// included, becomes a space, since a line break would submit the prompt early and an escape would
        /// reach the terminal), runs of spaces collapsed, trimmed, at most <see cref="MaxLength"/> characters.
        /// Null for nothing left.
        /// </summary>
        public static string Sanitize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var sb = new StringBuilder(Math.Min(text.Length, MaxLength + 1));
            bool lastSpace = false;
            foreach (char ch in text)
            {
                bool space = char.IsControl(ch) || char.IsWhiteSpace(ch);
                if (space)
                {
                    if (!lastSpace && sb.Length > 0)
                        sb.Append(' ');
                    lastSpace = true;
                }
                else
                {
                    sb.Append(ch);
                    lastSpace = false;
                }

                if (sb.Length >= MaxLength)
                    break;
            }

            // The loop stops at MaxLength, so no further cut is needed.
            string result = sb.ToString().Trim();
            // Cut in the middle of a surrogate pair: drop the orphan.
            if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1]))
                result = result.Substring(0, result.Length - 1);
            return result.Length == 0 ? null : result;
        }

        /// <summary>
        /// Whether a post-clear trigger from <paramref name="source"/> may deliver the note. Only the SessionStart
        /// hook's ("hook"): an agent's <c>/clear</c> is written to the ConPTY directly and never reaches the keystroke
        /// detector, so a "keystroke" trigger is a <c>/clear</c> somebody typed, not the clear the note was left
        /// for (pipeline Run 1: a stale note must not run in the wrong session).
        /// </summary>
        public static bool SourceMayDeliver(string source) => string.Equals(source, "hook", StringComparison.Ordinal);

        /// <summary>
        /// What to type after a clear, or null for nothing. A quiet-start pane gets nothing, except a note,
        /// typed with a plain head instead of the hook's text (no session-start in a quiet pane); any other
        /// pane gets <see cref="Compose"/> of the hook's text and the note.
        /// </summary>
        public static string PromptAfterClear(string baseText, string note, bool quietStart)
        {
            if (quietStart)
                return note == null ? null : Compose(null, note);
            return Compose(baseText, note);
        }

        /// <summary>
        /// The prompt typed after the clear: <paramref name="baseText"/> (the hook's text, or "initializing...")
        /// followed by the note, on one line. No note gives <paramref name="baseText"/> unchanged. The note is
        /// always LAST and introduced by exactly "Before clearing, you left yourself this to do next:": the
        /// plugin's helper /clear text tells the agent to look for that phrase at the end of the prompt.
        /// </summary>
        public static string Compose(string baseText, string note)
        {
            if (string.IsNullOrEmpty(note))
                return baseText;
            string head = string.IsNullOrWhiteSpace(baseText) ? "[MultiTerminal] Your context was cleared." : baseText.TrimEnd();
            return $"{head} Before clearing, you left yourself this to do next: {note}";
        }
    }
}
