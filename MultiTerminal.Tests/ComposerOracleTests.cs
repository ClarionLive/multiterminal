using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Covers the submission oracle (task 8b270b37, checklist item 3) — the first thing on this path
    /// that can observe its own failure case.
    ///
    /// <para><b>THE FACT THAT MATTERS IS
    /// <see cref="Text_visible_in_the_scrollback_after_a_successful_submit_is_not_read_as_an_unsent_prompt"/>.</b>
    /// There is exactly one way to get this oracle catastrophically wrong, and it is to invert it:
    /// Claude Code re-renders a SUBMITTED prompt as a user turn a row or two above the input box, so
    /// a check that merely asks "is this text on the screen" answers yes in the success case and
    /// reports every delivered prompt as undelivered. The two fixtures below differ in exactly that
    /// respect and nothing else.</para>
    ///
    /// <para><b>Falsified in both directions, and the prediction was wrong once.</b> Both runs
    /// confirmed first that the edit had actually removed the property, by occurrence count, rather
    /// than trusting that it looked broken (<c>.claude/rules/verification-discipline.md</c>).</para>
    ///
    /// <para><i>Direction 1 — search the whole screen instead of the box interior</i> (the specific
    /// wrong implementation described above). <b>Predicted exactly ONE red</b>, the scrollback fact,
    /// on the reasoning that every other fact either has the payload genuinely inside the box or
    /// returns Unknown before any searching happens. <b>TWO went red.</b> The extra was
    /// <see cref="Every_verdict_explains_itself"/>, which consumes the same submitted-screen fixture
    /// and so detects the same inversion through a different assertion — a real second detection, not
    /// a spurious one. The prediction was simply incomplete: it was made by reasoning about fixtures
    /// rather than about which facts consume them. Recorded rather than quietly corrected, because a
    /// count checked AFTER the run agrees with whatever number turns up, and the only value in
    /// forming one beforehand is being told when it was wrong.</para>
    ///
    /// <para><i>Direction 2 — never return NotConfirmed</i> (the opposite polarity: an oracle that
    /// can only ever clear a prompt). Predicted three reds —
    /// <see cref="Payload_still_sitting_in_the_input_box_is_reported_as_not_submitted"/>,
    /// <see cref="A_payload_wrapped_across_two_box_rows_is_still_found"/>, and
    /// <see cref="Every_verdict_explains_itself"/>, with the Unknown facts returning before the
    /// search and the scrollback fact already expecting Confirmed. Exactly those three went red.</para>
    ///
    /// <para><b>⚠️ WHAT THESE FACTS DO NOT PROVE.</b> Every one of them feeds hand-written rows to a
    /// pure function. None of them has seen a real Claude Code composer, so they pin the DECISION and
    /// say nothing about whether the rows a real pane produces look like these — in particular
    /// whether a queued prompt is echoed inside the box rather than outside it, which would be a
    /// false NotConfirmed that no fixture here can catch. That is live evidence, and it is
    /// deliberately not claimed.</para>
    /// </summary>
    public class ComposerOracleTests
    {
        /// <summary>A realistic spawned-helper job: one line, long enough to wrap in a box.</summary>
        private const string Job = "Investigate the flaky test in FooTests and report back with the root cause and a proposed fix.";

        // ──────────────────────────────────────────────── the inversion this must not make ──

        /// <summary>
        /// ⚠️ THE LOAD-BEARING FACT. After a successful submit the payload is STILL ON SCREEN — Claude
        /// Code renders it as a user turn, here four rows above the box. The box itself is empty.
        /// The verdict must be Confirmed.
        /// <para>An oracle built on "is the text visible" rather than "is the text in the box" answers
        /// NotConfirmed here, and would then press Enter and file a spawn_failed for every prompt that
        /// worked.</para>
        /// </summary>
        [Fact]
        public void Text_visible_in_the_scrollback_after_a_successful_submit_is_not_read_as_an_unsent_prompt()
        {
            var check = ComposerOracle.Evaluate(SubmittedScreen(), cursorRow: 5, ComposerOracle.Fingerprint(Job));

            Assert.Equal(SubmissionVerdict.Confirmed, check.Verdict);
        }

        /// <summary>
        /// ⚠️ THE SAME INVERSION AT ZERO SLACK — the widening the fact above CANNOT see.
        ///
        /// <para><b>First, the correction.</b> Item 5 was briefed on the premise that nothing standing
        /// would fail if the search were widened back to the whole screen. That premise is wrong, and
        /// the run disproves it: with the interior loop replaced by a whole-screen scan, FIVE facts go
        /// red, <see cref="Text_visible_in_the_scrollback_after_a_successful_submit_is_not_read_as_an_unsent_prompt"/>
        /// among them. The whole-screen widening was already covered.</para>
        ///
        /// <para><b>What was NOT covered is a narrow one.</b> That fixture parks the submitted prompt
        /// FOUR rows above the box, with two blank rows and an assistant line between, so an interior
        /// that starts a row or two too high still answers Confirmed on it — and a row or two is the
        /// LIVE shape: Claude Code re-renders a submitted prompt as a user turn directly above the
        /// composer. Here it sits on the row IMMEDIATELY above the top border, so the verdict stays
        /// Confirmed only while the interior starts no higher than the border row itself.</para>
        ///
        /// <para><b>Demonstrated, not argued:</b> starting the interior at <c>top - 1</c> instead of
        /// <c>top + 1</c> reds this fact and NOTHING else in the whole suite — 1161 of 1162 still
        /// green, including the four-row fixture above.</para>
        ///
        /// <para>The gap between the two fixtures is also ASSERTED rather than described, because a
        /// doc comment claiming "this one is tighter" is the least audited prose in the file and the
        /// green tick would be read as covering it (<c>.claude/rules/verification-discipline.md</c>).</para>
        /// </summary>
        [Fact]
        public void A_user_turn_on_the_row_immediately_above_the_box_is_not_read_as_an_unsent_prompt()
        {
            var rows = AdjacentUserTurnScreen();

            int turn = rows.FindIndex(r => r.Contains(Job, StringComparison.Ordinal));
            int top = rows.FindIndex(IsTopBorder);
            Assert.True(turn >= 0 && top >= 0, "The fixture no longer contains both a user turn and a box.");
            Assert.Equal(top - 1, turn);

            // The comparison that makes the claim above executable: the older fixture has slack, this
            // one has none. If SubmittedScreen is ever tightened, delete this assertion — do not
            // loosen this fixture to keep the contrast.
            var loose = SubmittedScreen();
            int looseTurn = loose.FindIndex(r => r.Contains(Job, StringComparison.Ordinal));
            int looseTop = loose.FindIndex(IsTopBorder);
            Assert.True(
                looseTop - looseTurn > 1,
                "SubmittedScreen no longer has rows of slack between the user turn and the box, so this "
                + "fixture is no longer the tighter of the two and its reason for existing is gone.");

            Assert.Equal(
                SubmissionVerdict.Confirmed,
                ComposerOracle.Evaluate(rows, cursorRow: 3, ComposerOracle.Fingerprint(Job)).Verdict);
        }

        /// <summary>
        /// The mirror bound. The interior stops strictly ABOVE the bottom border too, so a payload on
        /// the row immediately below the box is not searched either.
        ///
        /// <para><b>⚠️ WHAT THIS IS AND IS NOT.</b> It is a bound, not a live case: no observed Claude
        /// Code screen puts the payload below the composer, and nothing here should be read as saying
        /// one does. It exists because the start and the end of the interior loop are separate
        /// expressions and can drift apart one at a time — and when the end drifts, nothing else
        /// notices. Demonstrated: running the interior to <c>rows.Count</c> instead of <c>bottom</c>
        /// reds this fact and nothing else in the suite, the adjacency fact above included.</para>
        /// </summary>
        [Fact]
        public void A_payload_on_the_row_immediately_below_the_box_is_not_read_as_an_unsent_prompt()
        {
            var rows = EchoBelowTheBoxScreen();

            int echo = rows.FindIndex(r => r.Contains(Job, StringComparison.Ordinal));
            int bottom = rows.FindIndex(IsBottomBorder);
            Assert.True(echo >= 0 && bottom >= 0, "The fixture no longer contains both the payload and a box.");
            Assert.Equal(bottom + 1, echo);

            Assert.Equal(
                SubmissionVerdict.Confirmed,
                ComposerOracle.Evaluate(rows, cursorRow: 1, ComposerOracle.Fingerprint(Job)).Verdict);
        }

        /// <summary>
        /// The mirror image, and the failure the ticket exists for: the CR was taken as a newline, so
        /// the payload is sitting in the box with the cursor on a fresh line below it.
        /// <para>Same payload, same screen furniture, same cursor row — the ONLY difference from the
        /// fact above is which side of the box's top edge the text is on. If both facts do not hold
        /// together, the oracle is not reading the box, it is reading the screen.</para>
        /// </summary>
        [Fact]
        public void Payload_still_sitting_in_the_input_box_is_reported_as_not_submitted()
        {
            var check = ComposerOracle.Evaluate(UnsentScreen(), cursorRow: 5, ComposerOracle.Fingerprint(Job));

            Assert.Equal(SubmissionVerdict.NotConfirmed, check.Verdict);
        }

        /// <summary>
        /// The box wraps the payload across two of its rows, and pads both out to its own width. The
        /// tail therefore exists nowhere as a contiguous run of characters on any single row.
        /// <para>This is why the comparison is made on whitespace-and-box-glyph-stripped text rather
        /// than by matching a row: a per-row search finds nothing on a screen where the payload is
        /// plainly there, and answers Confirmed. <see cref="UnsentScreen"/> is that case, so this
        /// fact makes the reason explicit rather than adding coverage.</para>
        /// </summary>
        [Fact]
        public void A_payload_wrapped_across_two_box_rows_is_still_found()
        {
            var rows = UnsentScreen();

            Assert.DoesNotContain(rows, r => r.Contains(Job, StringComparison.Ordinal));
            Assert.Equal(
                SubmissionVerdict.NotConfirmed,
                ComposerOracle.Evaluate(rows, cursorRow: 5, ComposerOracle.Fingerprint(Job)).Verdict);
        }

        // ─────────────────────────────────────────────────────────── refusing to answer ──

        /// <summary>
        /// A pane at a shell prompt has no boxed composer. There is no input area to read, so there is
        /// no answer — and the payload IS on that screen, so an oracle that fell back to scanning
        /// rows would answer NotConfirmed and press Enter at a shell.
        /// </summary>
        [Fact]
        public void A_pane_with_no_input_box_is_Unknown_even_though_the_payload_is_on_screen()
        {
            var rows = new List<string>
            {
                "$ ls",
                "foo.txt  bar.txt",
                "$ " + Job,
            };

            var check = ComposerOracle.Evaluate(rows, cursorRow: 2, ComposerOracle.Fingerprint(Job));

            Assert.Equal(SubmissionVerdict.Unknown, check.Verdict);
        }

        /// <summary>
        /// The cursor is BELOW a completed box, so it is not inside one. Everything above it is
        /// scrollback by definition, and the oracle must say so rather than search it.
        /// </summary>
        [Fact]
        public void A_cursor_below_a_closed_box_is_Unknown_rather_than_searched_upwards()
        {
            var rows = new List<string>
            {
                "╭─────────╮",
                "│ > " + Job + " │",
                "╰─────────╯",
                "some later output",
            };

            var check = ComposerOracle.Evaluate(rows, cursorRow: 3, ComposerOracle.Fingerprint(Job));

            Assert.Equal(SubmissionVerdict.Unknown, check.Verdict);
        }

        /// <summary>
        /// A box drawn with no interior rows at all. There is nothing to read, which is not the same
        /// as reading nothing.
        /// </summary>
        [Fact]
        public void A_box_with_no_interior_rows_is_Unknown_not_Confirmed()
        {
            var rows = new List<string>
            {
                "╭──╮",
                "╰──╯",
            };

            Assert.Equal(
                SubmissionVerdict.Unknown,
                ComposerOracle.Evaluate(rows, cursorRow: 0, ComposerOracle.Fingerprint(Job)).Verdict);
        }

        /// <summary>
        /// ⚠️ THE GUARD THAT STOPS THE ORACLE SUBMITTING SOMEONE ELSE'S TYPING. A two-character
        /// payload matches a fragment of almost anything, and the response to NotConfirmed is to press
        /// Enter — so on a coincidental match the oracle would submit whatever the human had half
        /// written. Too little distinctive text is Unknown, never a guess.
        /// <para>The fixture is the case that would otherwise fire: the short payload's characters ARE
        /// present in the box, inside a completely unrelated word.</para>
        /// </summary>
        [Fact]
        public void A_payload_too_short_to_be_distinctive_is_Unknown_even_when_its_characters_are_in_the_box()
        {
            var rows = new List<string>
            {
                "╭───────────────╮",
                "│ > mongoose facts        │",
                "╰───────────────╯",
            };

            // "goo" is in "mongoose". A bare Contains would answer NotConfirmed.
            var check = ComposerOracle.Evaluate(rows, cursorRow: 1, ComposerOracle.Fingerprint("goo"));

            Assert.Equal(SubmissionVerdict.Unknown, check.Verdict);
            Assert.True(
                "goo".Length < ComposerOracle.MinFingerprintChars,
                "This fixture only tests the guard while its payload is under the floor.");
        }

        /// <summary>
        /// A cursor row outside the sample is a failure of the CHECK, not of the prompt. It means the
        /// page and the host disagree about what was sampled, and nothing downstream of that
        /// disagreement can be trusted.
        /// </summary>
        [Theory]
        [InlineData(99)]
        [InlineData(-1)]
        public void A_cursor_row_outside_the_sample_is_Unknown(int cursorRow)
        {
            var rows = new List<string> { "╭─╮", "│ > │", "╰─╯" };

            Assert.Equal(
                SubmissionVerdict.Unknown,
                ComposerOracle.Evaluate(rows, cursorRow, ComposerOracle.Fingerprint(Job)).Verdict);
        }

        /// <summary>An empty sample is Unknown — the page answered, but with nothing to read.</summary>
        [Fact]
        public void An_empty_sample_is_Unknown()
        {
            Assert.Equal(
                SubmissionVerdict.Unknown,
                ComposerOracle.Evaluate(Array.Empty<string>(), cursorRow: 0, ComposerOracle.Fingerprint(Job)).Verdict);

            Assert.Equal(
                SubmissionVerdict.Unknown,
                ComposerOracle.Evaluate(null, cursorRow: 0, ComposerOracle.Fingerprint(Job)).Verdict);
        }

        // ─────────────────────────────────────────────────────────────── the fingerprint ──

        /// <summary>
        /// The fingerprint is the END of the payload, not the beginning.
        /// <para>This is not a style choice. A long job scrolls inside the box, so only part of it is
        /// visible — and in the failure case the cursor is at the END of the typed text, which is what
        /// the stray newline put it there for. Matching the head would answer Confirmed for every
        /// payload longer than the box.</para>
        /// </summary>
        [Fact]
        public void The_fingerprint_is_the_tail_of_the_payload()
        {
            string longPayload = new string('a', 500) + "distinctive ending marker";
            string fp = ComposerOracle.Fingerprint(longPayload);

            Assert.EndsWith("distinctiveendingmarker", fp, StringComparison.Ordinal);
            Assert.True(fp.Length <= ComposerOracle.FingerprintChars);
            Assert.DoesNotContain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", fp, StringComparison.Ordinal);
        }

        /// <summary>
        /// Normalization is applied to BOTH sides of the comparison, which is what makes it safe to be
        /// lossy. Proven by construction rather than asserted in prose: the box's rendering of a
        /// payload — side glyphs, interior padding, a wrap that ate a space — normalizes to text that
        /// contains the payload's own normalization.
        /// </summary>
        [Fact]
        public void The_same_normalization_is_applied_to_the_box_and_to_the_payload()
        {
            string asRendered = "│ > Investigate the flaky test in FooTests and report back with │"
                              + "│   the root cause and a proposed fix.                          │";

            Assert.Contains(ComposerOracle.Normalize(Job), ComposerOracle.Normalize(asRendered), StringComparison.Ordinal);
        }

        /// <summary>
        /// Every verdict carries a reason, including the successful one. The first time this oracle is
        /// wrong, "how did it decide that" is the only question anyone will ask of it, and a bare enum
        /// cannot answer it.
        /// </summary>
        [Fact]
        public void Every_verdict_explains_itself()
        {
            var checks = new[]
            {
                ComposerOracle.Evaluate(SubmittedScreen(), 5, ComposerOracle.Fingerprint(Job)),
                ComposerOracle.Evaluate(UnsentScreen(), 5, ComposerOracle.Fingerprint(Job)),
                ComposerOracle.Evaluate(new[] { "$ nothing here" }, 0, ComposerOracle.Fingerprint(Job)),
            };

            Assert.Equal(3, checks.Select(c => c.Verdict).Distinct().Count());
            Assert.All(checks, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
        }

        /// <summary>
        /// ⚠️ "DO NOT RETYPE" IS ONLY SAID WHEN THE PAYLOAD WAS ACTUALLY SEEN IN THE BOX.
        ///
        /// <para>The advice travels with the verdict rather than being chosen at the reporting site,
        /// because <see cref="SubmissionVerdict.NotConfirmed"/> covers two situations needing
        /// opposite instructions — this class's "it is in the box" (do not retype, press Enter) and
        /// the renderer's "the typing was never acknowledged" (look first, a partial line may be
        /// there). A single hard-coded sentence at the reporting site is correct for at most one of
        /// them, and the recipient is an agent whose obvious response to an undelivered job is to
        /// send it again.</para>
        ///
        /// <para>This class may only ever produce the first. A verdict it is NOT positive about must
        /// carry no advice at all, so that the fallback sentence is used rather than a borrowed one.</para>
        /// </summary>
        [Fact]
        public void Only_a_confirmed_sighting_in_the_box_carries_the_do_not_retype_advice()
        {
            var seen = ComposerOracle.Evaluate(UnsentScreen(), 5, ComposerOracle.Fingerprint(Job));
            var notSeen = ComposerOracle.Evaluate(SubmittedScreen(), 5, ComposerOracle.Fingerprint(Job));
            var couldNotLook = ComposerOracle.Evaluate(new[] { "$ nothing here" }, 0, ComposerOracle.Fingerprint(Job));

            Assert.Equal(ComposerOracle.PayloadIsInTheBoxAdvice, seen.Advice);
            Assert.Contains("Do NOT retype", seen.Advice, StringComparison.Ordinal);

            Assert.Equal(string.Empty, notSeen.Advice);
            Assert.Equal(string.Empty, couldNotLook.Advice);
        }

        // ──────────────────────────────────────────────────────── the cross-file contract ──

        /// <summary>
        /// The probe function's name is a string contract across the WebView2 boundary that no
        /// compiler checks, and its failure is SILENT in the worst way: rename either half and
        /// <c>ExecuteScriptAsync</c> answers the literal <c>"null"</c>, the host maps that to Unknown,
        /// and every prompt from then on is unverifiable — with nothing failing anywhere.
        ///
        /// <para>Both sides are scanned with comments removed, because each file explains this
        /// function at length in prose and an unstripped scan would be satisfied by the explanation.
        /// The page is checked for an ASSIGNMENT and the host for an INVOCATION, so a mere mention
        /// cannot satisfy either half even if the stripping misses a line.</para>
        ///
        /// <para>Also pins the sample window to <see cref="ComposerOracle"/>'s own constants: the
        /// window the page reads and the window the oracle documents have to be the same one, and two
        /// hand-typed numbers drift.</para>
        /// </summary>
        [Fact]
        public void The_page_exports_the_probe_the_host_calls()
        {
            string page = StripLineComments(File.ReadAllText(RepoPath("Terminal", "terminal.html")));
            string host = StripLineComments(File.ReadAllText(RepoPath("Terminal", "WebViewTerminalRenderer.cs")));

            Assert.True(page.Length > 2000, "terminal.html came back too small to have been read properly.");
            Assert.True(host.Length > 2000, "WebViewTerminalRenderer.cs came back too small to have been read properly.");

            Assert.Contains("window.mtSampleComposerRegion = ", page, StringComparison.Ordinal);
            Assert.Contains("window.mtSampleComposerRegion(", host, StringComparison.Ordinal);

            Assert.Contains("ComposerOracle.SampleRowsAbove", host, StringComparison.Ordinal);
            Assert.Contains("ComposerOracle.SampleRowsBelow", host, StringComparison.Ordinal);
        }

        // ─────────────────────────────────────────────────────────────────────── fixtures ──

        /// <summary>
        /// A pane one moment after a SUCCESSFUL submit. The payload is on screen as a user turn; the
        /// box below it is empty and holds the cursor (row 5).
        /// </summary>
        private static List<string> SubmittedScreen() => new()
        {
            "> " + Job,
            string.Empty,
            "⏺ I'll start by reading the test file.",
            string.Empty,
            "╭───────────────────────────────╮",
            "│ >                              │",
            "╰───────────────────────────────╯",
            "  ? for shortcuts",
        };

        /// <summary>
        /// The same pane one moment after the FAILURE: the CR was taken as a newline, so the payload
        /// is wrapped across the box's first two interior rows and the cursor sits on the blank third
        /// one (row 5).
        /// </summary>
        private static List<string> UnsentScreen() => new()
        {
            "✻ Initializing…",
            string.Empty,
            "╭───────────────────────────────╮",
            "│ > Investigate the flaky test in FooTests and report back with │",
            "│   the root cause and a proposed fix.                          │",
            "│                                                               │",
            "╰───────────────────────────────╯",
            "  ? for shortcuts",
        };

        /// <summary>
        /// The same successful submit as <see cref="SubmittedScreen"/> with the slack removed: the
        /// user turn is on the row IMMEDIATELY above the box's top border, which is how a real pane
        /// often renders it. The box is empty and holds the cursor (row 3).
        /// </summary>
        private static List<string> AdjacentUserTurnScreen() => new()
        {
            "⏺ Done — I'll start on that now.",
            "> " + Job,
            "╭───────────────────────────────╮",
            "│ >                              │",
            "╰───────────────────────────────╯",
            "  ? for shortcuts",
        };

        /// <summary>
        /// The payload on the row IMMEDIATELY below the box's bottom border, cursor inside the empty
        /// box (row 1). Constructed to pin the lower bound, not copied from an observed screen.
        /// </summary>
        private static List<string> EchoBelowTheBoxScreen() => new()
        {
            "╭───────────────────────────────╮",
            "│ >                              │",
            "╰───────────────────────────────╯",
            "> " + Job,
        };

        // ───────────────────────────────────────────────────────────────────────── helpers ──

        private static bool IsTopBorder(string row) => row.TrimStart().StartsWith('╭');

        private static bool IsBottomBorder(string row) => row.TrimStart().StartsWith('╰');

        /// <summary>
        /// Drops block comments and whole-line <c>//</c> / <c>///</c> comments — the same three lines
        /// as <c>TypeAckCorrelationTests.Strip</c>, and carrying the same known residual: a TRAILING
        /// comment on a line of real code survives, because stripping those naively corrupts every
        /// string literal containing <c>//</c>. The fix is a tokenizer and a repo-wide change, not a
        /// per-file one.
        /// </summary>
        private static string StripLineComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string RepoPath(params string[] parts)
        {
            string here = Path.GetDirectoryName(ThisFile()) ?? ".";
            string[] segments = new string[parts.Length + 2];
            segments[0] = here;
            segments[1] = "..";
            Array.Copy(parts, 0, segments, 2, parts.Length);

            string path = Path.GetFullPath(Path.Combine(segments));
            Assert.True(File.Exists(path), $"Could not locate '{string.Join('/', parts)}' at '{path}'.");
            return path;
        }

        private static string ThisFile([CallerFilePath] string path = "") => path;
    }
}
