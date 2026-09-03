using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Clicking an Attention Rail card must focus THAT agent's terminal, even while a second
    /// document transiently shares its title (task edcdcdd5, Owner's live pass 2026-09-03).
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE DEFECT: the Owner clicked a newly created agent's card and focus jumped to the Home tab.
    /// Long-running terminals (Alice, Charlie) were fine; only the new one failed; and deleting the
    /// Home tab fixed it in every layout. That last fact is what identified the cause — a lookup
    /// that simply found nothing would activate nothing, and deleting an unrelated tab could not
    /// change the outcome.
    /// </para>
    /// <para>
    /// THE CAUSE: item 7 of this same ticket gives a terminal its title at PRE-registration, before
    /// the process launches — deliberately, so a card appears with its terminal instead of ~10-15s
    /// later. That opens a window in which TWO <c>TerminalDocument</c>s carry the same
    /// <c>CustomTitle</c>: the one still showing the start screen and the real terminal.
    /// <c>FirstOrDefault</c> took whichever the collection yielded first.
    /// </para>
    /// <para>
    /// WHY THIS IS A SOURCE-LEVEL TEST: <c>FocusTerminalForSession</c> reaches into
    /// <c>_dockPanel.Documents</c> and calls <c>Activate()</c> on a WinForms <c>DockContent</c>.
    /// Constructing that is not a unit test. What CAN be pinned is that the disambiguation exists
    /// and has the right polarity — a live terminal preferred over a start screen, not the reverse.
    /// Assertions run over comment-stripped source, following <see cref="BoardHudDoorwayTests"/>:
    /// the remarks above name every symbol involved, so a raw Contains() would be satisfied by this
    /// very prose and would pass against reverted code.
    /// </para>
    /// </remarks>
    public class FocusTargetDisambiguationTests
    {
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var kept = noBlocks
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            return string.Join("\n", kept);
        }

        private static string ReadStripped(params string[] relativeParts)
        {
            string path = Path.Combine(new[] { RepoRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Could not locate '{Path.Combine(relativeParts)}' at '{path}'.");
            return StripComments(File.ReadAllText(path));
        }

        private static string FocusMethodBody()
        {
            string src = ReadStripped("MainForm.cs");

            int start = src.IndexOf("private bool FocusTerminalForSession", StringComparison.Ordinal);
            Assert.True(start >= 0, "Could not find FocusTerminalForSession in MainForm.cs.");

            int open = src.IndexOf('{', start);
            Assert.True(open >= 0, "Found FocusTerminalForSession but no opening brace.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(open, i - open + 1);
                }
            }

            Assert.Fail("FocusTerminalForSession body was never brace-balanced.");
            return null;
        }

        /// <summary>
        /// The fix itself. A live terminal must be preferred; the start-screen document is the
        /// fallback, used only when it is the sole candidate (still a real terminal, just not
        /// booted yet).
        /// </summary>
        [Fact]
        public void Focus_prefers_a_live_terminal_over_one_still_showing_the_start_screen()
        {
            string body = FocusMethodBody();

            Assert.Contains("IsStartScreenVisible", body, StringComparison.Ordinal);
            Assert.Contains("!d.IsStartScreenVisible", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// POLARITY, which is the half a plain "mentions IsStartScreenVisible" assertion cannot
        /// catch. Preferring the START SCREEN would reproduce the reported bug exactly while still
        /// naming the property, so the negation is what carries the meaning.
        /// </summary>
        [Fact]
        public void The_preference_is_not_inverted()
        {
            string body = FocusMethodBody();

            int negated = body.IndexOf("!d.IsStartScreenVisible", StringComparison.Ordinal);
            Assert.True(negated >= 0, "The live-terminal preference is missing or no longer negated.");

            // A non-negated `d.IsStartScreenVisible` predicate would select the start screen. The
            // only legitimate occurrences are the negated one and the trace's reporting of what
            // was chosen, so anything selecting on the bare property is the inverted bug.
            Assert.DoesNotContain("FirstOrDefault(d => d.IsStartScreenVisible", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Where(d => d.IsStartScreenVisible", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The bug's actual shape: a single unordered pick across candidates. If someone collapses
        /// the two-step selection back into one <c>FirstOrDefault</c> over the title match, the
        /// arbitrary choice returns — and it returns silently, because the wrong terminal simply
        /// comes forward.
        /// </summary>
        [Fact]
        public void The_title_match_no_longer_resolves_by_a_single_arbitrary_pick()
        {
            string body = FocusMethodBody();

            Assert.DoesNotContain(
                ".FirstOrDefault(d => string.Equals(d.CustomTitle",
                body,
                StringComparison.Ordinal);

            // The candidate set has to be materialised for a second pass to be possible at all.
            Assert.Contains("Where(d => string.Equals(d.CustomTitle", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The duplicate-title window is a CONSEQUENCE of pre-registration timing, so it can widen
        /// again if that timing changes — and its symptom (focus lands on the wrong terminal) is
        /// silent. The diagnostic must survive the fix, because this session could not settle the
        /// cause from logs: the pre-existing trace fired only on the no-match path and said nothing
        /// about a WRONG match.
        /// </summary>
        [Fact]
        public void An_ambiguous_match_is_still_reported()
        {
            string body = FocusMethodBody();

            Assert.Contains("candidates.Count > 1", body, StringComparison.Ordinal);
            Assert.Contains("_debugLogService", body, StringComparison.Ordinal);
        }
    }
}
