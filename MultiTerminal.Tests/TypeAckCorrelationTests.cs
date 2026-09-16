using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Covers typing-job acknowledgment correlation (task 8b270b37, checklist item 2) — the plumbing
    /// that makes <c>TypeInputViaXtermAsync</c> able to report anything at all. Before it, the whole
    /// chain (renderer → <c>TerminalControl</c> → <c>TerminalDocument</c>) was <c>void</c>, so a
    /// prompt that was never typed and a prompt that was typed were the same observation.
    ///
    /// <para><b>⚠️ WHAT THESE FACTS DO NOT PROVE, stated first because the temptation is the whole
    /// point of the ticket.</b> None of them says anything about SUBMISSION. A true from this path
    /// means the characters reached the terminal's input path and the page said so. The failure that
    /// opened this ticket — one helper in three with its prompt typed and sitting unsent in the
    /// composer, because the trailing CR was taken as a newline — produces a true here and would keep
    /// every fact below green. An oracle for that is checklist item 3.</para>
    ///
    /// <para>The cross-file facts scan <c>Terminal/terminal.html</c>, because the job id is a string
    /// contract across a WebView2 boundary that no compiler checks: the C# half can be perfect while
    /// the page never sends the field. Precedent for a C#-named class binding to that page:
    /// <c>EnterAckCorrelationTests</c> and
    /// <c>InitialPromptTriggerWiringTests.Terminal_html_serializes_typing_through_one_queue</c>.</para>
    /// </summary>
    public class TypeAckCorrelationTests
    {
        // ─────────────────────────────────────────────────────────── registry behaviour ──

        /// <summary>
        /// THE POSITIVE PROOF that the id path is the one normally taken, and that the completion
        /// value is the page's answer rather than a constant.
        /// </summary>
        [Fact]
        public async Task An_ack_carrying_a_job_id_completes_that_job_through_the_correlated_path()
        {
            var registry = new TypeAckRegistry();
            var (jobId, ack) = registry.Register();

            Assert.Equal(TypeAckOutcome.Correlated, registry.Complete(jobId, typed: true));
            Assert.True(await ack);
            Assert.Equal(0, registry.OutstandingCount);
        }

        /// <summary>
        /// A <c>typeAbort</c> resolves the SAME waiter with the opposite answer. Without this the page
        /// would have no way to say "I dropped this job" and the caller would sit out the entire
        /// <see cref="TypeAckRegistry.AckBudgetMs"/> budget — up to ten minutes — to reach the same
        /// false.
        /// </summary>
        [Fact]
        public async Task An_abort_completes_the_same_job_as_not_typed()
        {
            var registry = new TypeAckRegistry();
            var (jobId, ack) = registry.Register();

            Assert.Equal(TypeAckOutcome.Correlated, registry.Complete(jobId, typed: false));
            Assert.False(await ack);
        }

        /// <summary>
        /// ⚠️ THE DEFECT THIS CLASS EXISTS TO PREVENT, borrowed wholesale from the Enter path
        /// (task f420feeb): with one shared waiter, job B's ack released job A's wait and A concluded
        /// its own text had been typed.
        ///
        /// <para><b>BOTH directions are asserted in separate registries, and that is not
        /// belt-and-braces.</b> The equivalent Enter fact PASSED against a deliberately broken
        /// take-any implementation, because the dictionary happened to enumerate the right bucket for
        /// those two keys. A single direction is satisfiable by luck; acking each job in its own
        /// registry forces a take-any implementation to be wrong in at least one of them whichever way
        /// the buckets fall.</para>
        ///
        /// <para>Completion is checked with <c>IsCompleted</c> rather than <c>await</c>: awaiting a
        /// waiter that should NOT have completed hangs the suite, and a hang produces no verdict, gets
        /// blamed on CI and ends in quarantine. A failed assertion produces one.</para>
        /// </summary>
        [Fact]
        public void An_ack_for_one_job_leaves_every_other_jobs_waiter_untouched()
        {
            // Direction 1: ack the FIRST job.
            var first = new TypeAckRegistry();
            var (idA, ackA) = first.Register();
            var (_, ackB) = first.Register();

            Assert.Equal(TypeAckOutcome.Correlated, first.Complete(idA, typed: true));
            Assert.True(ackA.IsCompleted);
            Assert.False(ackB.IsCompleted);

            // Direction 2: ack the SECOND job, in a registry of its own.
            var second = new TypeAckRegistry();
            var (_, ackC) = second.Register();
            var (idD, ackD) = second.Register();

            Assert.Equal(TypeAckOutcome.Correlated, second.Complete(idD, typed: true));
            Assert.True(ackD.IsCompleted);
            Assert.False(ackC.IsCompleted);
        }

        /// <summary>
        /// ⚠️ THE ASYMMETRY WITH <see cref="EnterAckRegistry"/>, AND THE REASON FOR IT. That class
        /// RESCUES an ack carrying no id when exactly one waiter is outstanding, because a
        /// <c>terminal.html</c> predating task f420feeb still sends <c>enterAck</c> — just without the
        /// field. Nothing analogous can happen here: no page has ever sent <c>typeAck</c> at all, so
        /// an id-less one has no benign origin and can only mean the echo is broken.
        ///
        /// <para>Rescuing it would be the exact trap <see cref="EnterAckRegistry"/>'s own remarks
        /// describe: every typing job would complete promptly, the suite would stay green, and the
        /// correlation would never run once. So the ack is dropped and the outstanding waiter is left
        /// strictly alone — asserted here, not just the outcome code, because an implementation that
        /// returned <see cref="TypeAckOutcome.Uncorrelated"/> and completed the waiter anyway would
        /// satisfy an outcome-only check.</para>
        /// </summary>
        [Fact]
        public void An_ack_with_no_id_is_dropped_and_rescues_nothing()
        {
            var registry = new TypeAckRegistry();
            var (_, ack) = registry.Register();

            Assert.Equal(TypeAckOutcome.Uncorrelated, registry.Complete(null, typed: true));
            Assert.Equal(TypeAckOutcome.Uncorrelated, registry.Complete(string.Empty, typed: true));

            Assert.False(ack.IsCompleted);
            Assert.Equal(1, registry.OutstandingCount);
        }

        /// <summary>
        /// An ack for a job that already timed out and was released must not release a LATER caller's
        /// wait — that is cross-talk arriving by the back door.
        /// </summary>
        [Fact]
        public void A_late_ack_for_a_released_job_is_dropped_and_does_not_release_a_later_caller()
        {
            var registry = new TypeAckRegistry();
            var (timedOut, _) = registry.Register();
            registry.Release(timedOut);

            var (_, later) = registry.Register();

            Assert.Equal(TypeAckOutcome.NoWaiter, registry.Complete(timedOut, typed: true));
            Assert.False(later.IsCompleted);
            Assert.Equal(1, registry.OutstandingCount);
        }

        /// <summary>
        /// An id that is not byte-for-byte identical matches nothing. A permissive comparison in front
        /// of an exact lookup is a bug generator (task c28e6177), and the id round-trips through a
        /// hand-rolled JSON reader that could plausibly acquire a trim one day.
        /// </summary>
        [Fact]
        public void An_id_that_is_not_byte_for_byte_identical_matches_nothing()
        {
            var registry = new TypeAckRegistry();
            var (jobId, ack) = registry.Register();

            Assert.Equal(TypeAckOutcome.NoWaiter, registry.Complete(jobId + " ", typed: true));
            Assert.Equal(TypeAckOutcome.NoWaiter, registry.Complete(" " + jobId, typed: true));
            Assert.False(ack.IsCompleted);

            Assert.Equal(TypeAckOutcome.Correlated, registry.Complete(jobId, typed: true));
        }

        /// <summary>
        /// A STRUCTURAL pin rather than a source scan for a comparer name: the class is asked which
        /// comparer its table actually uses. A textual version has both failure directions — it goes
        /// red when someone names a forbidden comparer in a refusal comment, which is this codebase's
        /// house style.
        /// <para>⚠️ HONEST SCOPE (the lesson of the equivalent Enter pin): this pins what it pins. It
        /// rules out the case-folding and culture-sensitive comparers below. It is NOT a general
        /// statement that any future comparer would be caught.</para>
        /// </summary>
        [Fact]
        public void The_waiter_table_compares_keys_exactly()
        {
            var comparer = new TypeAckRegistry().KeyComparer;

            Assert.False(comparer.Equals("7", "7 "));
            Assert.False(comparer.Equals("a", "A"));

            // Composed vs decomposed Å: equal under InvariantCulture, distinct under an exact
            // comparison. Included because running candidate comparers against the Enter pin showed
            // InvariantCulture satisfying every other assertion in it.
            Assert.False(comparer.Equals("Å", "Å"));

            Assert.True(comparer.Equals("7", "7"));
        }

        /// <summary>
        /// Ids are minted per registry and are distinct within it. (Across registries they collide
        /// freely and harmlessly — see <see cref="TypeAckRegistry.Register"/>.)
        /// </summary>
        [Fact]
        public void Every_job_in_one_registry_gets_a_distinct_id()
        {
            var registry = new TypeAckRegistry();
            var ids = Enumerable.Range(0, 50).Select(_ => registry.Register().JobId).ToList();

            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }

        // ──────────────────────────────────────────────────────────────── ack budget ──

        /// <summary>
        /// The budget must SCALE with the payload. A fixed constant is the obvious implementation and
        /// it is wrong: a 2,000-character job prompt at 5ms/char takes ten seconds to type, so any
        /// 3s-style budget borrowed from the Enter path would report "not typed" for every single
        /// spawned-helper delivery — manufacturing exactly the false failure report this ticket is
        /// trying to make impossible.
        /// </summary>
        [Fact]
        public void The_ack_budget_scales_with_the_payload_and_never_sits_below_the_typing_time()
        {
            Assert.True(
                TypeAckRegistry.AckBudgetMs(2000, 5) > 2000 * 5,
                "The budget for a 2,000-byte payload at 5ms/char is shorter than the typing itself takes.");

            Assert.True(
                TypeAckRegistry.AckBudgetMs(5000, 20) > TypeAckRegistry.AckBudgetMs(500, 20),
                "The budget does not grow with the payload.");
        }

        /// <summary>
        /// Floor and ceiling. The floor covers the short payloads (a 14-character banner) where the
        /// predicted typing time is nearly nothing but the page may still be queued behind another
        /// job; the ceiling is the deliberate bounded-wrong-answer described on
        /// <see cref="TypeAckRegistry.AckBudgetMs"/>.
        /// <para>The floor is asserted as an INEQUALITY for every payload and as an equality only for
        /// the degenerate empty one — writing the exact arithmetic for a 14-byte banner would pin the
        /// formula rather than the property, and go red on any harmless re-tuning.</para>
        /// </summary>
        [Fact]
        public void The_ack_budget_is_floored_and_capped()
        {
            Assert.Equal(30_000, TypeAckRegistry.AckBudgetMs(0, 0));
            Assert.True(TypeAckRegistry.AckBudgetMs(14, 20) >= 30_000);
            Assert.True(TypeAckRegistry.AckBudgetMs(2000, 5) >= 30_000);
            Assert.Equal(600_000, TypeAckRegistry.AckBudgetMs(int.MaxValue, 1000));
        }

        // ────────────────────────────────────────────────── cross-file string contract ──

        /// <summary>
        /// ⚠️ THE FACT THAT CLOSES THE TRAP. Every registry fact above passes whether or not the page
        /// sends an id — with the echo broken, every ack arrives id-less, every one is dropped, and
        /// the symptom is a silent timeout on a path whose whole purpose is to stop silent failures.
        ///
        /// <para>What makes "every ack carries the id" true here is STRUCTURAL rather than a habit
        /// applied at each site: there is exactly ONE place in the page that sends a typing ack, and
        /// it takes the id as a parameter. The Enter path has two ack sites and needed a count to
        /// check both. This asserts the single-site property itself, so a second ack site added
        /// anywhere fails — including one that carries the id correctly, because that would restore
        /// the shape where a third can be added without one.</para>
        /// </summary>
        [Fact]
        public void Terminal_html_sends_a_typing_ack_from_exactly_one_place_and_it_carries_the_id()
        {
            string src = Strip(ReadTerminalHtml());

            int fn = src.IndexOf("function ackTypeJob", StringComparison.Ordinal);
            Assert.True(fn >= 0, "terminal.html no longer has an ackTypeJob function.");

            string body = BraceMatchedBody(src, fn, "ackTypeJob");
            Assert.True(body.Length > 40, $"Extracted only {body.Length} chars for ackTypeJob; every assertion below is now vacuous.");

            Assert.Contains("typeJobId: jobId", body, StringComparison.Ordinal);

            Assert.Equal(CountOf(src, "typeAck"), CountOf(body, "typeAck"));
            Assert.Equal(CountOf(src, "typeAbort"), CountOf(body, "typeAbort"));
        }

        /// <summary>
        /// The ack must be sent where the LAST character has been sent, not where the job is queued.
        /// Acking at enqueue time would report "typed" for characters still sitting in the queue —
        /// the same species of premature claim as the one this ticket exists to remove, one layer
        /// down.
        /// </summary>
        [Fact]
        public void The_ack_is_sent_from_the_drain_completion_point_and_not_from_enqueue()
        {
            string src = Strip(ReadTerminalHtml());

            string enqueue = BraceMatchedBody(src, src.IndexOf("function enqueueTypeInput", StringComparison.Ordinal), "enqueueTypeInput");
            string drain = BraceMatchedBody(src, src.IndexOf("function drainTypeQueue", StringComparison.Ordinal), "drainTypeQueue");

            Assert.True(drain.Length > 400, $"Extracted only {drain.Length} chars for drainTypeQueue; the assertions below are vacuous.");

            Assert.Contains("ackTypeJob(job.jobId, true)", drain, StringComparison.Ordinal);
            Assert.DoesNotContain("ackTypeJob", enqueue, StringComparison.Ordinal);
        }

        /// <summary>
        /// The field name is a string contract across the WebView2 boundary: the page writes
        /// <c>typeJobId</c>, and the host's hand-rolled JSON reader matches property names
        /// lower-cased. A rename on either side compiles cleanly and silently disables correlation.
        /// </summary>
        [Fact]
        public void The_host_reads_the_same_field_name_the_page_writes()
        {
            Assert.Contains("typeJobId", Strip(ReadTerminalHtml()), StringComparison.Ordinal);

            string renderer = StripCSharp(File.ReadAllText(RepoPath("Terminal", "WebViewTerminalRenderer.cs")));
            Assert.Contains("case \"typejobid\":", renderer, StringComparison.Ordinal);
            Assert.Contains("msg.TypeJobId = value", renderer, StringComparison.Ordinal);
        }

        /// <summary>
        /// Both message types the page can send must be dispatched, and they must land on opposite
        /// answers. Handling only <c>typeAck</c> would turn a dropped job back into a silent timeout.
        /// </summary>
        [Fact]
        public void The_host_dispatches_both_the_ack_and_the_abort_to_opposite_answers()
        {
            string renderer = StripCSharp(File.ReadAllText(RepoPath("Terminal", "WebViewTerminalRenderer.cs")));

            Assert.Contains("case \"typeAck\":", renderer, StringComparison.Ordinal);
            Assert.Contains("case \"typeAbort\":", renderer, StringComparison.Ordinal);
            Assert.Contains("OnTypeAcknowledged(message.TypeJobId, typed: true)", renderer, StringComparison.Ordinal);
            Assert.Contains("OnTypeAcknowledged(message.TypeJobId, typed: false)", renderer, StringComparison.Ordinal);
        }

        /// <summary>
        /// The wire format is three colon-delimited fields before the base64, and both halves have to
        /// agree on that or typing stops entirely. Asserted as ARITY on the page side — the number of
        /// things <c>enqueueTypeInput</c> is given — rather than by pinning the exact parsing
        /// expressions, so that rewriting the parse does not go red while a genuine field-count
        /// disagreement still does.
        /// </summary>
        [Fact]
        public void The_host_and_the_page_agree_on_the_typeInput_wire_format()
        {
            string renderer = StripCSharp(File.ReadAllText(RepoPath("Terminal", "WebViewTerminalRenderer.cs")));
            Assert.Contains("\"typeInput:{jobId}:{charDelayMs}:{base64}\"", renderer, StringComparison.Ordinal);

            string src = Strip(ReadTerminalHtml());
            Assert.Contains("function enqueueTypeInput(jobId, decoded, charDelay)", src, StringComparison.Ordinal);

            int handler = src.IndexOf("case 'typeInput':", StringComparison.Ordinal);
            Assert.True(handler >= 0, "The typeInput message handler is gone from terminal.html.");
            int nextCase = src.IndexOf("case '", handler + 10, StringComparison.Ordinal);
            string handlerBody = nextCase > handler ? src[handler..nextCase] : src[handler..];

            Assert.Matches(new Regex(@"enqueueTypeInput\(\s*\w+\s*,[^;]+,[^;]+\)"), handlerBody);
        }

        // ───────────────────────────────────────────────────────────────────── helpers ──

        private static string ReadTerminalHtml() => File.ReadAllText(RepoPath("Terminal", "terminal.html"));

        /// <summary>
        /// Drops whole-line <c>//</c> comments, the same three lines as
        /// <c>InjectionPathCensusTests.Strip</c>. Necessary because every identifier these facts scan
        /// for is discussed at length in the surrounding prose — the page's own comments explain what
        /// <c>typeAck</c> does and does not mean — so an unstripped scan would be satisfied by the
        /// explanation instead of the code.
        /// <para>⚠️ KNOWN RESIDUAL, shared with every census idiom in this repo: a line is dropped only
        /// when its FIRST non-whitespace is <c>//</c>, so a TRAILING comment on a line of real code
        /// survives and can still satisfy a scan. Stripping those naively corrupts string literals
        /// containing <c>//</c> — every URL in this page's script tags — so the fix is a tokenizer,
        /// not a regex, and that is a repo-wide change rather than a per-file one.</para>
        /// </summary>
        private static string Strip(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string StripCSharp(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l =>
                {
                    string t = l.TrimStart();
                    return !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith("///", StringComparison.Ordinal);
                }));
        }

        private static int CountOf(string haystack, string needle)
        {
            int n = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                n++;
            }

            return n;
        }

        private static string BraceMatchedBody(string src, int start, string label)
        {
            Assert.True(start >= 0, $"'{label}' not found in terminal.html.");

            int open = src.IndexOf('{', start);
            Assert.True(open > start, $"No opening brace found for '{label}'.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{')
                {
                    depth++;
                }
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src[open..(i + 1)];
                }
            }

            Assert.Fail($"Braces never balanced for '{label}'.");
            return string.Empty;
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
