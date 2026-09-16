using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the wiring of a spawned helper's job delivery inside
    /// <c>MainForm.QueueInitialPromptDelivery</c>: the readiness trigger (task 7806024f) and, since task
    /// 8b270b37, delivery over the helper's CHANNEL instead of typing into its pane. Plus one cross-file
    /// fact about <c>Terminal/terminal.html</c>'s typing queue, which still serialises every other
    /// <c>typeInput</c> (the <c>initializing...</c> banner among them). Precedent: <c>BoardHudDoorwayTests</c>
    /// likewise binds a C#-named census class to a panel's HTML.
    ///
    /// <para>⚠️ SCOPE OF THE QUEUE FACT: it observes the <c>typeInput</c>/xterm path ONLY.
    /// <c>TerminalControl.InjectInputAsync</c> writes STRAIGHT to ConPTY and never reaches the queue;
    /// <c>InjectionPathCensusTests</c> enumerates those callers and <c>EnterAckCorrelationTests</c> covers
    /// the Enter-acknowledgment half.</para>
    ///
    /// <para><b>Why a source census.</b> <c>QueueInitialPromptDelivery</c> is private in an 8K+ LOC
    /// WinForms file that cannot be instantiated in a test. The readiness DECISION was extracted into
    /// <see cref="MultiTerminal.Services.HelperReadinessTrigger"/> and is unit-tested properly; what
    /// cannot be reached any other way is whether that decision is CALLED, what the delivery does with
    /// the channel's answer, and whether handlers are removed again.</para>
    ///
    /// <para><b>Scoped to the METHOD, and comments stripped first.</b> A file-wide scan would be
    /// satisfied by <c>MainForm</c>'s other, unrelated <c>TerminalRegistered</c> subscription and its other
    /// <c>DeliverViaChannel</c> callers. And the method is heavily commented with the very identifiers
    /// being asserted — including, deliberately, the words "typed" and "TypeInput" in its history — so
    /// an unstripped scan would pass or fail on prose.</para>
    /// </summary>
    public class InitialPromptTriggerWiringTests
    {
        /// <summary>
        /// The channel send inside the delivery method, as it is spelled in <c>MainForm.cs</c>. Several
        /// facts below locate it, so it is named once here rather than transcribed into each.
        /// <para>⚠️ A rename makes those facts fail LOUDLY on "the job is never sent" — keep it that
        /// way. Do not loosen it to any <c>DeliverViaChannel</c>: MainForm has other callers of that
        /// method, and the facts are about THIS one.</para>
        /// </summary>
        private const string ChannelCall = "DeliverViaChannel(channelPort, spawnerName, job,";

        /// <summary>
        /// The trigger must be subscribed, or a spawned helper's job waits out the 120s timer — the
        /// defect task 7806024f removed.
        /// </summary>
        [Fact]
        public void The_readiness_trigger_is_subscribed_to_terminal_registered()
        {
            string body = DeliveryMethodBody();

            Assert.Contains("TerminalRegistered += onRegistered", body, StringComparison.Ordinal);
            Assert.Contains("HelperReadinessTrigger.IsHelperAlive", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ THE SUBTLE ONE. Subscribing alone is not enough: the channel port can already be set by the
        /// time delivery is queued, and an event-only trigger would then wait for a registration that has
        /// ALREADY happened — restoring the full 120s stall in a narrower window.
        /// </summary>
        [Fact]
        public void The_current_row_is_checked_once_after_subscribing()
        {
            string body = DeliveryMethodBody();

            // Match the post-subscribe check's OWN identifier: the 120s fallback also calls GetTerminal
            // later in this method, and an assertion on that call passed with the check deleted.
            int subscribe = body.IndexOf("TerminalRegistered += onRegistered", StringComparison.Ordinal);
            int check = body.IndexOf("alreadyLive", StringComparison.Ordinal);

            Assert.True(subscribe >= 0, "The readiness trigger is not subscribed at all.");
            Assert.True(
                check >= 0,
                "No post-subscribe check of the current row. A helper whose channel port is already set "
                + "would wait for a registration event that has already fired.");
            Assert.True(
                check > subscribe,
                "The current-row check runs BEFORE the subscribe, so a registration landing between the "
                + "check and the subscribe is missed entirely. Subscribe first — Deliver's Interlocked "
                + "guard makes the double fire a no-op.");
        }

        /// <summary>
        /// Both exit paths must unsubscribe. <c>TerminalRegistered</c> is a long-lived broker event and
        /// every spawn adds a closure; leaking one per spawn accumulates silently for the life of the
        /// process, invisible because <c>Deliver</c>'s guard swallows every later fire.
        /// </summary>
        [Fact]
        public void Both_exit_paths_unsubscribe_the_trigger()
        {
            string body = DeliveryMethodBody();

            int unsubscribes = Regex.Matches(body, @"TerminalRegistered\s*-=\s*onRegistered").Count;

            Assert.True(
                unsubscribes >= 2,
                $"Found {unsubscribes} unsubscribe site(s); expected one in Deliver and one in GiveUp, "
                + "or every spawn leaks a closure onto a process-lifetime event.");
        }

        /// <summary>
        /// The 120s fallback stays — it is the give-up bound, and it is what produces the
        /// <c>spawn_failed</c> inbox message when a helper never boots.
        /// </summary>
        [Fact]
        public void The_give_up_timer_is_retained()
        {
            string body = DeliveryMethodBody();

            Assert.Contains("fallbackMs", body, StringComparison.Ordinal);
            Assert.Contains("GiveUp(", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Guards the census itself: if the method can no longer be located, every assertion here would
        /// fail for the wrong reason, or pass against an empty string.
        /// </summary>
        [Fact]
        public void The_method_body_was_actually_located()
        {
            string body = DeliveryMethodBody();

            Assert.True(
                body.Length > 2000,
                $"Extracted only {body.Length} chars for QueueInitialPromptDelivery. The method was "
                + "renamed or the brace matching broke; every other fact in this file is now vacuous.");
        }

        /// <summary>
        /// ⚠️ THE JOB IS SENT OVER THE CHANNEL AND NOTHING IN THIS METHOD TYPES (task 8b270b37).
        /// Typing was the defect: a trailing CR landing in Claude Code's turn-start transition became a
        /// newline in the composer and left the job unsent while MT logged it delivered — 1 in 3 in the
        /// burst that opened the ticket. Live on 2026-09-16 the channel carried 15/15 tool-using jobs.
        ///
        /// <para>The channel call is asserted PRESENT in the same fact. That is the discriminator: without
        /// it, a slicing bug returning the wrong region would satisfy the two <c>DoesNotContain</c>s and
        /// this fact would prove nothing. The absences are asserted on the STRIPPED body because the
        /// method's own comment explains the typing history by name.</para>
        /// </summary>
        [Fact]
        public void The_job_is_sent_over_the_channel_and_never_typed()
        {
            string body = DeliveryMethodBody();

            Assert.Contains(ChannelCall, body, StringComparison.Ordinal);
            Assert.DoesNotContain("TypeInput", body, StringComparison.Ordinal);
            Assert.DoesNotContain("WaitForRendererReadyAsync", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ ONE MESSAGE ID FOR EVERY ATTEMPT. The channel server records an id only after injecting the
        /// message and answers a repeat with 200 <c>duplicate_ignored</c>, so a retry with the SAME id
        /// cannot deliver a job twice. An id minted per attempt would turn every retry after a
        /// response-lost success into a second copy of the job.
        ///
        /// <para>Asserted structurally: the id is minted exactly once, BEFORE <c>Deliver</c> is declared
        /// (so not inside the retry loop), and the channel call passes that variable.</para>
        /// </summary>
        [Fact]
        public void Every_retry_reuses_one_message_id()
        {
            string body = DeliveryMethodBody();

            int minted = body.IndexOf("string messageId =", StringComparison.Ordinal);
            int deliver = body.IndexOf("void Deliver(", StringComparison.Ordinal);
            int call = body.IndexOf(ChannelCall, StringComparison.Ordinal);

            Assert.True(minted >= 0, "No messageId is minted for the job, so retries are not deduplicated.");
            Assert.True(deliver >= 0 && call >= 0, "Deliver or its channel call could not be located.");
            Assert.True(
                minted < deliver,
                "The message id is minted inside Deliver or later — per attempt or per trigger, not once "
                + "per job — so the channel server cannot recognise a retry as a repeat.");
            Assert.Single(Regex.Matches(body, @"Guid\.NewGuid\("));

            string callArgs = body[call..body.IndexOf(')', call)];
            Assert.Contains("messageId", callArgs, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ SUCCESS IS LOGGED ONLY ON THE CHANNEL'S ANSWER. The line that opened this ticket was a
        /// success-shaped "Delivering initial prompt" written before anything had confirmed anything. The
        /// Info line announcing acceptance must sit inside the branch taken when the send returned true.
        /// </summary>
        [Fact]
        public void Success_is_logged_only_after_the_channel_accepts()
        {
            string body = DeliveryMethodBody();

            Assert.True(
                Regex.IsMatch(
                    body,
                    @"if\s*\(\s*await\s+" + Regex.Escape(ChannelCall) + @"[^;]*?\)\s*\{\s*_debugLogService\?\.Info\(",
                    RegexOptions.Singleline),
                "The acceptance log is not the first statement of the branch taken when the channel send "
                + "returned true. A success line anywhere else can be written for a job that was never "
                + "accepted — the defect this ticket exists to remove.");
        }

        /// <summary>
        /// ⚠️ THE EXACTLY-ONCE GUARD COMES FIRST. <c>Deliver</c> takes it before unsubscribing and before
        /// sending, so two triggers cannot both send. That ordering also sets the price of firing on the
        /// wrong evidence: once taken, the other triggers are gone — which is why the readiness predicate
        /// keys on the docId (task c28e6177).
        /// </summary>
        [Fact]
        public void The_exactly_once_guard_is_taken_before_any_trigger_is_disarmed_or_anything_is_sent()
        {
            string body = DeliveryMethodBody();

            int guard = body.IndexOf("Interlocked.Exchange(ref delivered", StringComparison.Ordinal);
            int unsubscribe = body.IndexOf("TerminalRegistered -= onRegistered", StringComparison.Ordinal);
            int send = body.IndexOf(ChannelCall, StringComparison.Ordinal);

            Assert.True(guard >= 0, "Deliver no longer takes an exactly-once guard; two triggers can both send the job.");
            Assert.True(unsubscribe >= 0, "Deliver does not unsubscribe the registration trigger.");
            Assert.True(send >= 0, "The job is never sent at all.");
            Assert.True(guard < unsubscribe, "The guard is taken AFTER the handler is removed.");
            Assert.True(guard < send, "The guard is taken AFTER the send. Exactly-once no longer protects the send.");
        }

        /// <summary>
        /// Every way the job can fail to go must reach the spawner: the send exhausting its retries, the
        /// send throwing, and the 120s give-up. Before task 7806024f's split, failures after the guard
        /// logged and returned, and <c>GiveUp</c> — the only writer of <c>spawn_failed</c> — could never
        /// run, because the guard it checks was already set.
        /// </summary>
        [Fact]
        public void Every_delivery_failure_path_reports_to_the_spawner()
        {
            string body = DeliveryMethodBody();

            Assert.Contains("void ReportUndelivered", body, StringComparison.Ordinal);

            // GiveUp must delegate rather than carry its own copy of the inbox-routing code.
            int giveUp = body.IndexOf("void GiveUp", StringComparison.Ordinal);
            Assert.True(giveUp >= 0, "GiveUp is gone — the 120s give-up no longer notifies anyone.");
            Assert.Contains("ReportUndelivered(reason)", body[giveUp..], StringComparison.Ordinal);

            // The retries-exhausted exit: the first statement after the retry loop is a report. Located
            // by brace matching, because the loop's own body also contains statements after its break.
            int loop = body.IndexOf("for (int attempt", StringComparison.Ordinal);
            Assert.True(loop >= 0, "The channel retry loop could not be located.");
            int loopEnd = MatchingBrace(body, body.IndexOf('{', loop));
            Assert.True(
                Regex.IsMatch(body[(loopEnd + 1)..], @"^\s*ReportUndelivered\("),
                "The statement after the channel retry loop is not ReportUndelivered — a job the channel "
                + "refused on every attempt would vanish with only a log line.");

            // The throwing send.
            Assert.True(
                Regex.IsMatch(body, @"catch\s*\(\s*Exception\s+ex\s*\)\s*\{\s*ReportUndelivered\(", RegexOptions.Singleline),
                "A send that throws does not call ReportUndelivered.");
        }

        /// <summary>Index of the brace closing the one at <paramref name="open"/>.</summary>
        private static int MatchingBrace(string src, int open)
        {
            Assert.True(open >= 0 && src[open] == '{', "No opening brace where one was expected.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return i;
            }

            Assert.Fail("Braces never balanced.");
            return -1;
        }
        /// <summary>
        /// ⚠️ REMOVAL PROOF for task <c>c28e6177</c>: readiness is decided on the pane's DOCID, and no
        /// call in this method decides it on a display name.
        ///
        /// <para><b>Why the census and not the unit tests.</b> <see cref="MultiTerminal.Services.HelperReadinessTrigger"/>
        /// takes three parameters, two of which are <c>string</c>. Re-keying it changed what those strings
        /// MEAN and not one thing the compiler can see — passing <c>row.Name</c> to a parameter named
        /// <c>registeredDocId</c> builds cleanly and silently restores the defect. The unit tests cannot
        /// catch it either, because they only ever see the values a caller chose to hand over. The only
        /// place the binding is observable is the call site, and the call site is private inside an 8K+ LOC
        /// form.</para>
        ///
        /// <para>The lookups are asserted too, not just the predicate. <c>GetTerminal</c> resolves a
        /// terminal id, a docId OR a name, so a fixed predicate fed from <c>GetTerminal(agentName)</c>
        /// would still be reading a row that a whitespace- or case-variant name could resolve to.</para>
        /// </summary>
        [Fact]
        public void Readiness_is_decided_on_the_docid_and_never_on_a_display_name()
        {
            string body = DeliveryMethodBody();

            var calls = Regex.Matches(body, @"IsHelperAlive\(\s*([A-Za-z_][\w.]*)\s*,");
            Assert.True(
                calls.Count >= 3,
                $"Found {calls.Count} IsHelperAlive call(s); expected 3 — the registration handler, the "
                + "post-subscribe check and the fallback timer. A missing one means a trigger stopped "
                + "consulting the shared rule and is deciding readiness for itself again.");

            foreach (Match call in calls)
            {
                string firstArg = call.Groups[1].Value;
                Assert.True(
                    firstArg.EndsWith(".DocId", StringComparison.Ordinal),
                    $"IsHelperAlive is passed '{firstArg}' as the registered identity. It must be a "
                    + ".DocId. Display names are not identities here: the broker never trims one, so a "
                    + "helper spawned as \"Alice \" while \"Alice\" is live is TWO rows to the broker, and "
                    + "keying on the name lets the live Alice's registration deliver that helper's job "
                    + "(task c28e6177).");
            }

            Assert.False(
                Regex.IsMatch(body, @"IsHelperAlive\([^)]*agentName"),
                "An IsHelperAlive call still passes agentName. The awaited side must be the docId of the "
                + "pane this spawn created; agentName is a display name and is only fit for log lines.");

            Assert.DoesNotContain(
                "GetTerminal(agentName)",
                body,
                StringComparison.Ordinal);
            Assert.Contains("GetTerminal(docId)", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ THE SAME GUARD, POINTED AT THE TEST THAT DEMONSTRATES THE FIX — added because the fact
        /// above was not enough, and the way it was not enough is instructive.
        ///
        /// <para><c>Readiness_is_decided_on_the_docid_and_never_on_a_display_name</c> exists because
        /// <c>IsHelperAlive</c>'s two identity parameters are <c>string</c>, so handing it a display name
        /// compiles at zero warnings and silently restores the defect. That guard scans
        /// <c>MainForm.cs</c>. It does not scan <c>HelperReadinessIdentityAsymmetryTests</c> — and that is
        /// exactly where the mistake landed: its call-site model kept passing <c>.Name</c> after the
        /// re-key, so all five of its facts stayed green while no longer demonstrating that the predicate
        /// keys on the pane at all. Its class doc meanwhile stated the line had been rewritten.</para>
        ///
        /// <para>A hazard worth guarding at the call site is worth guarding in the file built to prove
        /// the call site is right. Peer review caught this one; the point of the fact is that the suite
        /// should catch the next.</para>
        /// </summary>
        [Fact]
        public void The_asymmetry_tests_model_the_call_site_on_docids_too()
        {
            string here = Path.GetDirectoryName(ThisFile()) ?? ".";
            string path = Path.GetFullPath(Path.Combine(here, "HelperReadinessIdentityAsymmetryTests.cs"));
            Assert.True(File.Exists(path), $"Could not locate the asymmetry tests at '{path}'.");

            // Comments stripped for the same reason every other census here strips them: this file
            // DISCUSSES passing .Name at length, in prose, deliberately.
            string src = StripComments(File.ReadAllText(path));

            var calls = Regex.Matches(src, @"IsHelperAlive\(\s*([A-Za-z_][\w.]*)\s*,");
            Assert.True(calls.Count > 0, "No IsHelperAlive call found; this census is vacuous.");

            foreach (Match call in calls)
            {
                string firstArg = call.Groups[1].Value;
                Assert.True(
                    firstArg.EndsWith(".DocId", StringComparison.Ordinal),
                    $"The asymmetry tests pass '{firstArg}' as the registered identity. Both parameters "
                    + "are strings, so a display name compiles silently here and every fact in that file "
                    + "stays green while proving something weaker than it claims — it would then hold "
                    + "against any exact-match predicate, docId or not (task c28e6177).");
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
        /// ⚠️ CROSS-FILE, and the compiler checks none of it. The typing queue lives in
        /// <c>Terminal/terminal.html</c>; the C# side cannot observe it. Two concurrent typeInput
        /// payloads used to interleave their characters into one composer and submit two garbled
        /// prompts — reachable only once delivery moved from t+120s to t+~5s, into the same window as
        /// the "initializing..." injection.
        /// <para>Asserts the queue is USED, not merely defined: a handler that defines
        /// <c>enqueueTypeInput</c> and then still starts its own <c>typeNextChar()</c> chain is exactly
        /// the bug, and would pass a definition-only check.</para>
        /// </summary>
        [Fact]
        public void Terminal_html_serializes_typing_through_one_queue()
        {
            string here = Path.GetDirectoryName(ThisFile()) ?? ".";
            string path = Path.GetFullPath(Path.Combine(here, "..", "Terminal", "terminal.html"));
            Assert.True(File.Exists(path), $"Could not locate terminal.html at '{path}'.");

            string src = File.ReadAllText(path);

            Assert.Contains("function enqueueTypeInput", src, StringComparison.Ordinal);
            Assert.Contains("function drainTypeQueue", src, StringComparison.Ordinal);

            int handler = src.IndexOf("case 'typeInput':", StringComparison.Ordinal);
            Assert.True(handler >= 0, "The typeInput message handler is gone.");

            int nextCase = src.IndexOf("case '", handler + 10, StringComparison.Ordinal);
            string handlerBody = nextCase > handler ? src[handler..nextCase] : src[handler..];

            Assert.Contains("enqueueTypeInput(", handlerBody, StringComparison.Ordinal);
            Assert.DoesNotContain("typeNextChar()", handlerBody, StringComparison.Ordinal);

            // The next job may start ONLY from the previous one's completion. A drainTypeQueue call that
            // exists solely in enqueueTypeInput would let a second message run concurrently again.
            int drain = src.IndexOf("function drainTypeQueue", StringComparison.Ordinal);
            string drainBody = src[drain..Math.Min(src.Length, drain + 1200)];
            Assert.Contains("drainTypeQueue()", drainBody, StringComparison.Ordinal);
        }

        /// <summary>
        /// The body of <c>QueueInitialPromptDelivery</c>, comments stripped, located by brace matching
        /// from its signature.
        /// </summary>
        private static string DeliveryMethodBody()
        {
            string src = ReadMainFormStripped();

            int start = src.IndexOf("private void QueueInitialPromptDelivery", StringComparison.Ordinal);
            Assert.True(start >= 0, "QueueInitialPromptDelivery not found in MainForm.cs.");

            int open = src.IndexOf('{', start);
            Assert.True(open >= 0, "No opening brace after QueueInitialPromptDelivery's signature.");

            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src[open..(i + 1)];
                }
            }

            Assert.Fail("Braces never balanced for QueueInitialPromptDelivery.");
            return string.Empty;
        }

        /// <summary>
        /// MainForm.cs with block and line comments removed. Stripping is load-bearing: the wiring is
        /// documented with the same identifiers the assertions look for, so an unstripped scan would be
        /// satisfied by prose.
        /// </summary>
        private static string ReadMainFormStripped()
        {
            string here = Path.GetDirectoryName(ThisFile()) ?? ".";
            string path = Path.GetFullPath(Path.Combine(here, "..", "MainForm.cs"));
            Assert.True(File.Exists(path), $"Could not locate MainForm.cs at '{path}'.");

            string src = File.ReadAllText(path);
            string noBlocks = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);

            return string.Join(
                "\n",
                noBlocks.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        }

        private static string ThisFile([CallerFilePath] string p = "") => p;
    }
}
