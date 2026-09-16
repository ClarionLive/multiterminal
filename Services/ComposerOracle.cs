using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MultiTerminal.Services
{
    /// <summary>
    /// The answer to one question and one question only: <b>is the text we just typed still sitting
    /// in the terminal's input box?</b> (task 8b270b37, checklist item 3.)
    /// </summary>
    internal enum SubmissionVerdict
    {
        /// <summary>
        /// The payload is NOT in the input box. It left the composer — which it does on submission
        /// AND on enqueue, and those are both successes (see <see cref="ComposerOracle"/>).
        /// </summary>
        Confirmed,

        /// <summary>
        /// The payload IS still in the input box. This is the failure the ticket exists to catch: a
        /// trailing CR taken as a newline, leaving the prompt typed and unsent.
        /// </summary>
        NotConfirmed,

        /// <summary>
        /// The check could not be run or could not be trusted — the page did not answer, the input
        /// box could not be located, the payload had too little distinctive text to search for.
        /// <para>⚠️ NEVER collapse this into either of the others. Folded into Confirmed it hides
        /// every failure it could not see; folded into NotConfirmed it manufactures failures out of
        /// its own blind spots and then acts on them.</para>
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// A verdict and the reason for it. The reason is ALWAYS populated, including on
    /// <see cref="SubmissionVerdict.Confirmed"/>, because "how did it decide that" is the question
    /// asked of this class the first time it is wrong, and a verdict with no reason cannot answer it.
    /// </summary>
    internal readonly struct SubmissionCheck : IEquatable<SubmissionCheck>
    {
        private SubmissionCheck(SubmissionVerdict verdict, string reason, string advice)
        {
            this.Verdict = verdict;
            this.Reason = reason ?? string.Empty;
            this.Advice = advice ?? string.Empty;
        }

        internal SubmissionVerdict Verdict { get; }

        /// <summary>Human-readable, log-ready, and safe to put in front of an agent.</summary>
        internal string Reason { get; }

        /// <summary>
        /// What the recipient of a failure report should DO, written by whichever code established
        /// the failure. Empty when nobody has anything specific to say.
        ///
        /// <para><b>⚠️ WHY THIS IS CARRIED RATHER THAN DECIDED BY THE REPORTER.</b> The reporter
        /// (<c>MainForm</c>) sees a <see cref="SubmissionVerdict.NotConfirmed"/> and cannot tell
        /// which kind it is, and the two kinds need OPPOSITE advice. "The payload is sitting in the
        /// box" means <i>do not retype, press Enter</i>. "The typing was never acknowledged" means
        /// <i>look at the pane first, it may hold a partial line</i>. A single hard-coded sentence at
        /// the reporting site is correct for at most one of them — and a confidently wrong
        /// instruction to an agent holding an undelivered job is how this ticket's defect gets a
        /// second life one layer up.</para>
        /// </summary>
        internal string Advice { get; }

        internal static SubmissionCheck Confirmed(string reason) => new(SubmissionVerdict.Confirmed, reason, null);

        internal static SubmissionCheck NotConfirmed(string reason, string advice) => new(SubmissionVerdict.NotConfirmed, reason, advice);

        internal static SubmissionCheck Unknown(string reason) => new(SubmissionVerdict.Unknown, reason, null);

        public bool Equals(SubmissionCheck other) =>
            this.Verdict == other.Verdict
            && string.Equals(this.Reason, other.Reason, StringComparison.Ordinal)
            && string.Equals(this.Advice, other.Advice, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is SubmissionCheck other && this.Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            this.Verdict,
            StringComparer.Ordinal.GetHashCode(this.Reason ?? string.Empty),
            StringComparer.Ordinal.GetHashCode(this.Advice ?? string.Empty));

        public override string ToString() => $"{this.Verdict}: {this.Reason}";

        public static bool operator ==(SubmissionCheck left, SubmissionCheck right) => left.Equals(right);

        public static bool operator !=(SubmissionCheck left, SubmissionCheck right) => !left.Equals(right);
    }

    /// <summary>
    /// Decides whether a just-typed payload is still sitting unsent in the terminal's input box
    /// (task 8b270b37, checklist item 3).
    ///
    /// <para><b>THE QUESTION IT ASKS, AND WHY IT IS THAT ONE.</b> An Enter typed into Claude Code
    /// has three outcomes, not two: <i>submitted</i>, <i>enqueued</i> (Claude was mid-turn, so the
    /// prompt joins its queue and is answered next), and <i>lost</i> (the CR landed in the ~100ms
    /// window at the start of a turn where the input handler is switching from "Enter submits" to
    /// "Enter enqueues", and was taken as a literal newline). <b>Enqueued is a SUCCESS.</b> Measured
    /// on three helpers sent a byte-identical prompt in one burst: the one that failed was typed
    /// 130ms after its pane's turn began, the one that was enqueued and then answered was typed
    /// 842ms after.</para>
    ///
    /// <para>An oracle that reported enqueued as a failure and responded by retyping would deliver
    /// the job TWICE. This codebase has already paid for that mistake once — see the long comment at
    /// the task-drop fallback in <c>MainForm</c>. So this class deliberately does NOT classify three
    /// ways. Submitted and enqueued both CLEAR the input box; only the loss leaves the payload
    /// sitting in it. One falsifiable question covers the distinction that matters:
    /// <b>is the payload still in the box?</b></para>
    ///
    /// <para><b>⚠️ WHY IT IS NOT BUILT ON CURSOR MOVEMENT.</b> Because cursor movement cannot answer
    /// it. A CR that the composer takes as a newline moves the cursor to a new line exactly as a
    /// submission does. That inference is the defect item 1 of this ticket corrected in
    /// <c>Terminal/terminal.html</c>; rebuilding it here under a new name would be the same claim
    /// with a better title.</para>
    ///
    /// <para><b>⚠️ HOW "STILL IN THE BOX" IS TOLD APART FROM "ON SCREEN AFTER A SUCCESSFUL SUBMIT".</b>
    /// This is the one way to get the oracle exactly backwards, so it is structural rather than
    /// heuristic: <b>the scrollback is never searched at all.</b> A submitted prompt is re-rendered
    /// by Claude Code as a user turn ABOVE the input box — sometimes only two rows above it. This
    /// class searches ONLY the rows strictly BETWEEN the input box's own top and bottom edges, and
    /// only when the cursor is inside that box. A user turn lies outside those edges by construction
    /// and is therefore not merely unlikely to match — it is not in the searched text. If the box
    /// cannot be located around the cursor, the answer is <see cref="SubmissionVerdict.Unknown"/>;
    /// there is no path on which this class falls back to scanning the screen.</para>
    ///
    /// <para><b>Why a TAIL and not the whole payload.</b> The box wraps and, for a long prompt,
    /// scrolls — a 2,000-character job is never present in the visible box in full. In the failure
    /// case the cursor sits at the END of the typed text (that is what the stray newline did), so
    /// the END is the part guaranteed to be visible. Matching the head would miss every payload
    /// longer than the box.</para>
    ///
    /// <para><b>⚠️ THE BIAS IS TOWARDS <see cref="SubmissionVerdict.Confirmed"/>, DELIBERATELY.</b>
    /// A false NotConfirmed is acted on: the caller presses Enter again and files a failure report.
    /// If the box actually held something else — a half-typed line from the human — that Enter
    /// submits THEIR text. A false Confirmed only restores the status quo this ticket is improving
    /// on. So every ambiguity resolves away from acting.</para>
    ///
    /// <para><b>Pure by construction.</b> No broker, no WebView, no UI — it takes rows of text and
    /// returns a verdict, for the reason <see cref="EnterAckRegistry"/> and
    /// <see cref="TypeAckRegistry"/> exist: <c>WebViewTerminalRenderer</c> cannot be instantiated in
    /// a test, so a decision left inside it could only ever be asserted by scanning its source.</para>
    /// </summary>
    internal static class ComposerOracle
    {
        /// <summary>
        /// How many trailing characters of the payload are searched for.
        /// <para>Long enough that a coincidental match is not a practical concern, short enough to
        /// fit inside a narrow input box after wrapping.</para>
        /// </summary>
        internal const int FingerprintChars = 48;

        /// <summary>
        /// Below this many characters (after <see cref="Normalize"/>) the fingerprint is not
        /// distinctive enough to act on, and the verdict is <see cref="SubmissionVerdict.Unknown"/>
        /// rather than a guess.
        /// <para>⚠️ THIS IS WHAT STOPS THE ORACLE ACTING ON A COINCIDENCE. A three-character
        /// fingerprint would match a fragment of whatever the human happened to be typing, and the
        /// response to a NotConfirmed is to press Enter — submitting their line. The short fixed
        /// bootstrap string that D-shaped delivery (task 837e16a3) will send must therefore be at
        /// least this long after whitespace is removed; <c>initializing...</c>, the existing one, is
        /// fifteen.</para>
        /// </summary>
        internal const int MinFingerprintChars = 6;

        /// <summary>
        /// How many rows above the cursor the page is asked to sample. The input box's top edge must
        /// fall inside this window or the verdict is Unknown; a Claude Code composer holding a long
        /// pasted prompt is the tallest thing it has to cover.
        /// </summary>
        internal const int SampleRowsAbove = 150;

        /// <summary>
        /// How many rows below the cursor the page is asked to sample. The box's bottom edge is
        /// within a row or two of the cursor in every observed state; this is slack, not a guess.
        /// </summary>
        internal const int SampleRowsBelow = 12;

        /// <summary>
        /// The advice attached to the one verdict this class can be POSITIVE about: it looked in the
        /// box and the payload was there.
        /// <para>"Do not retype" is the load-bearing half. The recipient is an agent holding an
        /// undelivered job, and the obvious response to an undelivered job is to send it again — onto
        /// a composer that already contains it.</para>
        /// </summary>
        internal const string PayloadIsInTheBoxAdvice =
            "⚠️ The text WAS typed and is sitting UNSENT in that pane's composer. Do NOT retype it — that submits it twice. Press Enter in the pane, or tell the helper what to do directly.";

        // The two edges of a box, as drawn by anything that draws boxes. Rounded (Claude Code's
        // composer), square, and the double/single variants are all accepted: the cost of listing
        // them is nothing, and the cost of missing one is a permanent Unknown on some future TUI.
        private const string TopBorderChars = "╭┌╒╓╔"; // ╭ ┌ ╒ ╓ ╔
        private const string BottomBorderChars = "╰└╘╙╚"; // ╰ └ ╘ ╙ ╚

        /// <summary>
        /// The trailing slice of <paramref name="payloadText"/> that the box will be searched for.
        /// </summary>
        /// <param name="payloadText">
        /// The text as handed to the typing path, WITHOUT the line ending the renderer appends. The
        /// line ending is exactly the character that did not do its job, so it is not part of the
        /// evidence that it did not.
        /// </param>
        internal static string Fingerprint(string payloadText)
        {
            if (string.IsNullOrEmpty(payloadText))
            {
                return string.Empty;
            }

            // Take the tail in RAW characters and normalize afterwards, not the other way round:
            // normalizing first then slicing would silently change which part of the payload is
            // being matched depending on how much whitespace it happened to contain.
            string tail = payloadText.Length <= FingerprintChars
                ? payloadText
                : payloadText.Substring(payloadText.Length - FingerprintChars);

            return Normalize(tail);
        }

        /// <summary>
        /// Removes everything that the act of rendering into a box can change: whitespace (the box
        /// pads its interior, and a wrap at a word boundary eats the space that was there) and the
        /// box-drawing and block-element glyphs themselves (the edges, and any rule drawn inside).
        ///
        /// <para><b>The same function is applied to BOTH sides of the comparison</b>, which is what
        /// makes it safe. It is lossy — a payload that genuinely contained a <c>│</c> would have it
        /// stripped — but it is lossy identically on both sides, so the match cannot be skewed by it.
        /// The alternative, reconstructing the box's logical text by locating its side glyphs and
        /// re-joining wrapped rows, needs a rule for whether a wrap ate a space, and there is no such
        /// rule that is right in both directions.</para>
        /// </summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                // Box Drawing (U+2500–U+257F) and Block Elements (U+2580–U+259F).
                if (c >= '─' && c <= '▟')
                {
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Evaluates one sample of the terminal's screen.
        /// </summary>
        /// <param name="rows">
        /// Consecutive rows of the terminal buffer, top to bottom, as the page read them. Not the
        /// whole screen: a window around the cursor (see <see cref="SampleRowsAbove"/> /
        /// <see cref="SampleRowsBelow"/>).
        /// </param>
        /// <param name="cursorRow">Index into <paramref name="rows"/> of the row the cursor is on.</param>
        /// <param name="fingerprint">The output of <see cref="Fingerprint"/> for the typed payload.</param>
        internal static SubmissionCheck Evaluate(IReadOnlyList<string> rows, int cursorRow, string fingerprint)
        {
            if (rows == null || rows.Count == 0)
            {
                return SubmissionCheck.Unknown("the page returned no terminal rows to look at");
            }

            if (cursorRow < 0 || cursorRow >= rows.Count)
            {
                return SubmissionCheck.Unknown(
                    string.Format(CultureInfo.InvariantCulture, "the cursor row ({0}) was outside the {1} sampled rows", cursorRow, rows.Count));
            }

            string needle = Normalize(fingerprint);
            if (needle.Length < MinFingerprintChars)
            {
                return SubmissionCheck.Unknown(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the payload has only {0} distinctive characters ({1} are needed) — too few to search for without risking a coincidental match on someone else's typing",
                        needle.Length,
                        MinFingerprintChars));
            }

            // ── Locate the box the cursor is inside ──────────────────────────────────────────
            // Hitting the WRONG edge first is not "keep looking" — it is proof the cursor is not
            // inside a box, and therefore that anything found by searching on would be scrollback.
            // That is the case this whole method exists to refuse.
            int top = -1;
            for (int i = cursorRow; i >= 0; i--)
            {
                char edge = FirstVisible(rows[i]);
                if (i != cursorRow && BottomBorderChars.IndexOf(edge) >= 0)
                {
                    return SubmissionCheck.Unknown("the cursor is not inside an input box — a box's BOTTOM edge sits above it, so anything found above would be scrollback");
                }

                if (TopBorderChars.IndexOf(edge) >= 0)
                {
                    top = i;
                    break;
                }
            }

            if (top < 0)
            {
                return SubmissionCheck.Unknown("no input box could be located around the cursor (no top edge above it) — the pane may not be showing a boxed composer");
            }

            int bottom = -1;
            for (int i = cursorRow; i < rows.Count; i++)
            {
                char edge = FirstVisible(rows[i]);
                if (i != cursorRow && TopBorderChars.IndexOf(edge) >= 0)
                {
                    return SubmissionCheck.Unknown("the cursor is not inside an input box — a box's TOP edge sits below it");
                }

                if (BottomBorderChars.IndexOf(edge) >= 0)
                {
                    bottom = i;
                    break;
                }
            }

            if (bottom < 0)
            {
                return SubmissionCheck.Unknown("no input box could be located around the cursor (no bottom edge below it)");
            }

            if (bottom - top < 2)
            {
                return SubmissionCheck.Unknown("the input box has no interior rows to read");
            }

            // ── Read ONLY the box's interior ─────────────────────────────────────────────────
            var interior = new StringBuilder();
            for (int i = top + 1; i < bottom; i++)
            {
                interior.Append(rows[i]);
            }

            string haystack = Normalize(interior.ToString());

            if (haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
            {
                return SubmissionCheck.NotConfirmed(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the payload's last {0} characters are still sitting in the terminal's input box (rows {1}-{2} of the sample), so the Enter did not submit it and did not queue it",
                        needle.Length,
                        top + 1,
                        bottom - 1),
                    PayloadIsInTheBoxAdvice);
            }

            return SubmissionCheck.Confirmed(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the terminal's input box (rows {0}-{1} of the sample) does not contain the payload, so it was either submitted or queued",
                    top + 1,
                    bottom - 1));
        }

        /// <summary>
        /// The first non-whitespace character of a row, or <c>'\0'</c> for a blank row. Leading
        /// indentation is skipped because a box need not start at column zero.
        /// </summary>
        private static char FirstVisible(string row)
        {
            if (string.IsNullOrEmpty(row))
            {
                return '\0';
            }

            foreach (char c in row)
            {
                if (!char.IsWhiteSpace(c))
                {
                    return c;
                }
            }

            return '\0';
        }
    }
}
