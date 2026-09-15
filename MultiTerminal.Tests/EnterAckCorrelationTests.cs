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
    /// Covers Enter-key acknowledgment correlation (task f420feeb, census F2) — the defect where one
    /// shared <c>TaskCompletionSource</c> served every injection into a terminal.
    ///
    /// <para><b>What these facts must prove, and why the obvious test is not enough.</b> "An ack
    /// completes the wait promptly" is true of BOTH the correlated path and the compatibility path
    /// for an id-less ack. A suite that only checks completion would stay green if terminal.html's
    /// id echo were broken — every ack would arrive id-less, every one would be rescued by the
    /// compatibility branch, and the correlation would never run. That is this codebase's signature
    /// failure (green for the wrong reason), so the facts below assert WHICH PATH was taken, via
    /// <see cref="EnterAckOutcome"/>, rather than just the outcome the caller sees.</para>
    ///
    /// <para>The last two facts are cross-file, against <c>Terminal/terminal.html</c>: the id is a
    /// string contract across a WebView2 boundary that no compiler checks, and the C# half can be
    /// perfect while the page never sends the field. Precedent for binding a C#-named test class to
    /// that page: <c>InitialPromptTriggerWiringTests.Terminal_html_serializes_typing_through_one_queue</c>
    /// and <c>BoardHudDoorwayTests</c>. Cohesion by defect, not by file.</para>
    /// </summary>
    public class EnterAckCorrelationTests
    {
        /// <summary>
        /// THE POSITIVE PROOF that the id path is the one normally taken. Without this, the
        /// compatibility branch could be carrying the entire feature undetected.
        /// </summary>
        [Fact]
        public async Task An_ack_carrying_a_job_id_completes_that_job_through_the_correlated_path()
        {
            var registry = new EnterAckRegistry();
            var (jobId, ack) = registry.Register();

            Assert.Equal(EnterAckOutcome.Correlated, registry.Complete(jobId));
            Assert.True(await ack);
            Assert.Equal(0, registry.OutstandingCount);
        }

        /// <summary>
        /// ⚠️ THE DEFECT ITSELF. With one shared field, B's ack released A's wait and A concluded its
        /// own Enter had been processed — which the C# side then recorded as a successful delivery.
        ///
        /// <para><b>BOTH directions are asserted, and that is not belt-and-braces.</b> The first
        /// version of this fact acked only the second job, and it PASSED against a deliberately
        /// broken registry that ignored the id and released whichever waiter it found first — because
        /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> enumerates by
        /// hash bucket, and for these two keys that happened to be the right one. A single direction
        /// therefore proves nothing: it is satisfiable by luck. Acking each job in its own registry
        /// forces a take-any implementation to be wrong in at least one of them, whichever way the
        /// buckets fall. (Found by actually running the falsification rather than assuming it.)</para>
        ///
        /// <para>Completion is checked with <c>IsCompleted</c> rather than <c>await</c>: under a
        /// broken registry the wrong task completes and awaiting the right one would HANG the suite
        /// instead of failing it.</para>
        /// </summary>
        [Fact]
        public void An_ack_for_one_job_leaves_every_other_jobs_waiter_untouched()
        {
            // Direction 1: ack the FIRST job.
            var first = new EnterAckRegistry();
            var (firstA, ackFirstA) = first.Register();
            var (firstB, ackFirstB) = first.Register();
            Assert.NotEqual(firstA, firstB);

            Assert.Equal(EnterAckOutcome.Correlated, first.Complete(firstA));
            Assert.True(ackFirstA.IsCompleted, "Acking job A did not complete job A's waiter.");
            Assert.False(ackFirstB.IsCompleted, "Job B's wait was released by job A's ack — cross-talk.");

            // Direction 2: ack the SECOND job, in a fresh registry so ordering cannot be reused.
            var second = new EnterAckRegistry();
            var (secondA, ackSecondA) = second.Register();
            var (secondB, ackSecondB) = second.Register();

            Assert.Equal(EnterAckOutcome.Correlated, second.Complete(secondB));
            Assert.True(ackSecondB.IsCompleted, "Acking job B did not complete job B's waiter.");
            Assert.False(ackSecondA.IsCompleted, "Job A's wait was released by job B's ack — cross-talk.");
        }

        /// <summary>
        /// An attempt that already timed out has released its waiter. Its late ack must be dropped,
        /// not spent on whoever is waiting now — that would reintroduce cross-talk by another route.
        /// </summary>
        [Fact]
        public async Task A_late_ack_for_a_released_job_is_dropped_and_does_not_release_a_later_caller()
        {
            var registry = new EnterAckRegistry();
            var (timedOutId, _) = registry.Register();
            registry.Release(timedOutId);

            var (liveId, liveAck) = registry.Register();

            Assert.Equal(EnterAckOutcome.NoWaiter, registry.Complete(timedOutId));
            Assert.False(liveAck.IsCompleted, "A late ack from an abandoned attempt released a different caller's wait.");

            Assert.Equal(EnterAckOutcome.Correlated, registry.Complete(liveId));
            Assert.True(await liveAck);
        }

        /// <summary>
        /// The compatibility path works — and SAYS SO. The distinct outcome is the point: it is what
        /// lets the renderer log at Warning, and what lets a reader tell "a cached page is being
        /// tolerated" from "the id echo is broken and nothing is correlated".
        /// </summary>
        [Fact]
        public async Task An_id_less_ack_with_one_waiter_takes_the_compatibility_path_and_reports_it()
        {
            var registry = new EnterAckRegistry();
            var (_, ack) = registry.Register();

            Assert.Equal(EnterAckOutcome.CompatibilitySingleWaiter, registry.Complete(null));
            Assert.True(await ack);
        }

        /// <summary>
        /// With two waiters and no id there is no honest answer, so nothing is completed. The old
        /// code guessed — it released whichever happened to be in the field — and that guess is the
        /// bug. Dropping costs one 3s timeout; guessing costs a false "Enter processed".
        /// </summary>
        [Fact]
        public void An_id_less_ack_with_two_waiters_is_dropped_rather_than_guessed()
        {
            var registry = new EnterAckRegistry();
            var (_, firstAck) = registry.Register();
            var (_, secondAck) = registry.Register();

            Assert.Equal(EnterAckOutcome.AmbiguousDropped, registry.Complete(string.Empty));
            Assert.False(firstAck.IsCompleted);
            Assert.False(secondAck.IsCompleted);
            Assert.Equal(2, registry.OutstandingCount);
        }

        /// <summary>
        /// The lookup does not NORMALIZE: a padded or prefixed id finds nothing rather than being
        /// helpfully coerced into a match. A permissive comparison in front of an exact lookup is a
        /// bug generator rather than a safety margin (the general form of task c28e6177's finding).
        ///
        /// <para>⚠️ SCOPE, because the obvious reading is too generous and I checked: this fact does
        /// NOT catch a swap to <c>StringComparer.OrdinalIgnoreCase</c>. Minted ids come from an int
        /// counter, so they are digits, and digits have no case — every assertion here passes under
        /// either comparer. That was verified by making the swap and watching this stay green. The
        /// comparer itself is pinned by
        /// <see cref="The_waiter_lookup_is_pinned_to_an_ordinal_comparer"/> instead.</para>
        /// </summary>
        [Fact]
        public void An_id_that_is_not_byte_for_byte_identical_matches_nothing()
        {
            var registry = new EnterAckRegistry();
            var (jobId, ack) = registry.Register();

            Assert.Equal(EnterAckOutcome.NoWaiter, registry.Complete(jobId + " "));
            Assert.Equal(EnterAckOutcome.NoWaiter, registry.Complete(" " + jobId));
            Assert.Equal(EnterAckOutcome.NoWaiter, registry.Complete("x" + jobId));
            Assert.False(ack.IsCompleted, "A near-miss id released the waiter — the lookup is not exact.");

            Assert.Equal(EnterAckOutcome.Correlated, registry.Complete(jobId));
            Assert.True(ack.IsCompleted);
        }

        /// <summary>
        /// Pins the comparer the waiter table ACTUALLY USES, by asking it — no source scan.
        ///
        /// <para>⚠️ THIS REPLACED A TEXTUAL PIN THAT WAS A DEFECT IN ITS OWN RIGHT (peer review, this
        /// ticket). That version read <c>EnterAckRegistry.cs</c> and asserted the file did not contain
        /// "OrdinalIgnoreCase". A source scan for a FORBIDDEN name has a failure direction that a scan
        /// for a required one does not: writing the wrong answer's name in a REFUSAL COMMENT turns the
        /// test RED against completely correct code. This codebase writes exactly those comments — the
        /// registry's own comment about permissive comparisons comes within a word of it — so the test
        /// punished the documenting instinct, and the natural response to a red test is to change the
        /// CODE. A false pass costs a defect; a false failure costs the documentation and points the
        /// next reader at the wrong fix.</para>
        ///
        /// <para>Asserted behaviourally rather than by identity (<c>Assert.Same(StringComparer.Ordinal, …)</c>)
        /// so that any correctly case-sensitive, non-trimming comparer passes. What matters is what the
        /// table does, not which static it was handed.</para>
        ///
        /// <para>No behavioural fact in this class can reach this: minted ids are digits and therefore
        /// caseless, so a loosened comparer changes nothing observable today. It becomes live the moment
        /// the id is a Guid, a name or a composite — by which time the loosening is already in place.</para>
        /// </summary>
        [Fact]
        public void The_waiter_table_compares_keys_exactly()
        {
            var comparer = new EnterAckRegistry().KeyComparer;

            Assert.True(comparer.Equals("7", "7"));
            Assert.False(comparer.Equals("a", "A"));
            Assert.False(comparer.Equals("7 ", "7"));
            Assert.False(comparer.Equals(" 7", "7"));
        }

        /// <summary>
        /// ⚠️ THE COMPATIBILITY PATH MUST BE AUDIBLE, and nothing asserted that it was (peer review,
        /// this ticket). The enum value was pinned; the log that gives it its purpose was not, so
        /// downgrading the call to Trace — or deleting the case body outright — left the whole suite
        /// green.
        ///
        /// <para>That log is the ONLY runtime signal distinguishing "a cached page is being tolerated"
        /// from "the id echo is broken and nothing is correlated at all". Both look identical from
        /// outside: Enters complete promptly either way. Without it the shim can silently carry the
        /// entire feature, which is the exact failure the shim was reviewed for in the first place.</para>
        ///
        /// <para>A source census because <c>WebViewTerminalRenderer</c> is a WebView2-hosting
        /// <c>UserControl</c> and cannot be instantiated here. Comments stripped: the handler's own doc
        /// explains at length why it logs at Warning, so an unstripped scan would pass on the
        /// explanation rather than the call.</para>
        /// </summary>
        [Fact]
        public void The_renderer_logs_a_warning_when_the_compatibility_path_fires()
        {
            string src = StripComments(File.ReadAllText(RepoPath("Terminal", "WebViewTerminalRenderer.cs")));

            foreach (string outcome in new[] { "CompatibilitySingleWaiter", "AmbiguousDropped" })
            {
                int label = src.IndexOf("case EnterAckOutcome." + outcome + ":", StringComparison.Ordinal);
                Assert.True(label >= 0, $"The {outcome} case is gone from the ack handler.");

                int next = src.IndexOf("case ", label + 10, StringComparison.Ordinal);
                string arm = next > label ? src[label..next] : src[label..];

                Assert.True(
                    arm.Contains("Warning(", StringComparison.Ordinal),
                    $"The {outcome} arm no longer logs at Warning. That log is the only runtime signal "
                    + "separating a tolerated stale page from a broken id echo — without it the "
                    + "compatibility path can carry the whole feature silently, which is what it was "
                    + "reviewed for.");
            }
        }

        /// <summary>Block and line comments removed, so a census cannot be satisfied by prose.</summary>
        private static string StripComments(string src)
        {
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        /// <summary>
        /// A failed injection makes up to five attempts and the renderer outlives all of them, so the
        /// sender's <c>finally</c> must actually forget the job.
        /// </summary>
        [Fact]
        public void Releasing_a_job_removes_its_waiter()
        {
            var registry = new EnterAckRegistry();
            var (jobId, _) = registry.Register();
            Assert.Equal(1, registry.OutstandingCount);

            registry.Release(jobId);
            Assert.Equal(0, registry.OutstandingCount);
        }

        /// <summary>
        /// ⚠️ CROSS-FILE, AND THE ONE THAT CLOSES THE TRAP. Every C# fact above passes whether or not
        /// the page sends an id: with the echo broken, all acks arrive id-less and the compatibility
        /// branch rescues them silently. Both ack sites must echo — the cursor-movement success path
        /// AND the 500ms timeout path — because an echo on only one of them fails exactly the same
        /// way, just less often.
        /// </summary>
        [Fact]
        public void Terminal_html_echoes_the_job_id_on_every_ack_site()
        {
            string src = ReadTerminalHtml();

            int handler = src.IndexOf("case 'sendEnter':", StringComparison.Ordinal);
            Assert.True(handler >= 0, "The sendEnter message handler is gone from terminal.html.");

            int nextCase = src.IndexOf("case '", handler + 10, StringComparison.Ordinal);
            string handlerBody = nextCase > handler ? src[handler..nextCase] : src[handler..];

            Assert.Contains("const enterJobId = data", handlerBody, StringComparison.Ordinal);

            // Count the ack sends and require EVERY one to carry the id, rather than asserting that
            // the string appears somewhere in the body — which one correct site would satisfy while
            // the other stayed bare.
            int acks = 0;
            int correlated = 0;
            for (int i = handlerBody.IndexOf("type: 'enterAck'", StringComparison.Ordinal); i >= 0;
                 i = handlerBody.IndexOf("type: 'enterAck'", i + 1, StringComparison.Ordinal))
            {
                acks++;
                int lineEnd = handlerBody.IndexOf('\n', i);
                string line = lineEnd > i ? handlerBody[i..lineEnd] : handlerBody[i..];
                if (line.Contains("enterJobId", StringComparison.Ordinal)) correlated++;
            }

            Assert.True(acks >= 2, $"Expected both the cursor-movement and timeout ack sites; found {acks}.");
            Assert.True(
                acks == correlated,
                $"{acks - correlated} of {acks} enterAck sends carry no job id. Those acks land in EnterAckRegistry's compatibility branch, which completes them anyway — so this does NOT fail the build by itself, and nothing else would have caught it.");
        }

        /// <summary>
        /// The field name is a string contract across the WebView2 boundary: the page writes
        /// <c>enterJobId</c>, and the host's hand-rolled JSON reader matches property names
        /// lower-cased. A rename on either side compiles cleanly and silently disables correlation.
        /// </summary>
        [Fact]
        public void The_host_reads_the_same_field_name_the_page_writes()
        {
            Assert.Contains("enterJobId", ReadTerminalHtml(), StringComparison.Ordinal);

            string renderer = File.ReadAllText(RepoPath("Terminal", "WebViewTerminalRenderer.cs"));
            Assert.Contains("case \"enterjobid\":", renderer, StringComparison.Ordinal);
            Assert.Contains("EnterJobId", renderer, StringComparison.Ordinal);
        }

        private static string ReadTerminalHtml() => File.ReadAllText(RepoPath("Terminal", "terminal.html"));

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
