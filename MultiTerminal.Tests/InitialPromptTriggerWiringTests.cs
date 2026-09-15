using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins the wiring of the spawned-helper readiness trigger inside
    /// <c>MainForm.QueueInitialPromptDelivery</c> (task 7806024f).
    ///
    /// <para><b>Why a source census.</b> <c>QueueInitialPromptDelivery</c> is private in an 8K+ LOC
    /// WinForms file that cannot be instantiated in a test. The DECISION it makes was extracted into
    /// <see cref="MultiTerminal.Services.HelperReadinessTrigger"/> and is unit-tested properly; what
    /// cannot be reached any other way is whether that decision is actually CALLED, and whether the
    /// handler is removed again. This is the same technique
    /// <c>AgentActivityObservationTests.MainForm_constructs_starts_and_disposes_the_activity_watcher</c>
    /// already uses against this file.</para>
    ///
    /// <para><b>Scoped to the METHOD, and comments stripped first.</b> Both matter. A file-wide scan
    /// would be satisfied by <c>MainForm</c>'s other, unrelated <c>TerminalRegistered</c> subscription at
    /// startup — the "already-listed file hides the real site" failure this codebase has now been bitten
    /// by three times. And the wiring is heavily commented with the very identifiers being asserted, so
    /// an unstripped scan would pass on prose alone: exactly the defect found in task 2ddfc32f, where a
    /// census was satisfied by a <c>&lt;see cref&gt;</c> in a doc comment.</para>
    /// </summary>
    public class InitialPromptTriggerWiringTests
    {
        /// <summary>
        /// The trigger must be subscribed, or a spawned helper's job waits out the 120s timer — the
        /// defect this ticket exists to remove.
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
        /// time delivery is queued (a fast helper, or a name reused from a row that is still live), and an
        /// event-only trigger would then wait for a registration that has ALREADY happened — restoring the
        /// full 120s stall in a narrower window. Nothing else in the suite can observe this, and its
        /// absence looks exactly like working code.
        /// </summary>
        [Fact]
        public void The_current_row_is_checked_once_after_subscribing()
        {
            string body = DeliveryMethodBody();

            // ⚠️ Match the post-subscribe check's OWN identifier, not `GetTerminal(agentName)`. The 120s
            // fallback calls that too, later in this same method — so the obvious assertion was satisfied
            // by the WRONG call site and passed with the check deleted. Caught by falsifying this very
            // fact (5/5 green with the line removed), which is the only reason it is not still vacuous.
            int subscribe = body.IndexOf("TerminalRegistered += onRegistered", StringComparison.Ordinal);
            int check = body.IndexOf("alreadyLive", StringComparison.Ordinal);

            Assert.True(subscribe >= 0, "The readiness trigger is not subscribed at all.");
            Assert.True(
                check >= 0,
                "No post-subscribe check of the current row. A helper whose channel port is already set "
                + "would wait for a registration event that has already fired — the 120s stall this "
                + "ticket removes, reintroduced in a narrower window.");
            Assert.True(
                check > subscribe,
                "The current-row check runs BEFORE the subscribe. That leaves the mirror-image gap: a "
                + "registration landing between the check and the subscribe is missed entirely. Subscribe "
                + "first — the overlap is free, because Deliver's Interlocked guard makes a double fire a "
                + "no-op.");
        }

        /// <summary>
        /// Both exit paths must unsubscribe. <c>TerminalRegistered</c> is a long-lived broker event and
        /// every spawn adds a closure; leaking one per spawn accumulates silently for the life of the
        /// process, and the symptom (a handler firing for a helper whose delivery is long settled) is
        /// invisible because <c>Deliver</c>'s guard swallows it.
        /// </summary>
        [Fact]
        public void Both_exit_paths_unsubscribe_the_trigger()
        {
            string body = DeliveryMethodBody();

            int unsubscribes = Regex.Matches(body, @"TerminalRegistered\s*-=\s*onRegistered").Count;

            Assert.True(
                unsubscribes >= 2,
                $"Found {unsubscribes} unsubscribe site(s); expected one in Deliver and one in GiveUp. "
                + "The existing NotificationReceived handler is removed on both paths and this one must "
                + "match, or every spawn leaks a closure onto a process-lifetime event.");
        }

        /// <summary>
        /// The 120s fallback stays — it is the give-up bound, a different job from the trigger, and it is
        /// what still produces the <c>spawn_failed</c> inbox message when a helper never boots (77d1182f
        /// items 9/10). Removing it while "fixing the 120s wait" would be an easy and expensive mistake.
        /// </summary>
        [Fact]
        public void The_give_up_timer_is_retained()
        {
            string body = DeliveryMethodBody();

            Assert.Contains("fallbackMs", body, StringComparison.Ordinal);
            Assert.Contains("GiveUp(", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Guards the census itself: if the method can no longer be located the extraction returns
        /// something short, and every assertion above would fail for the wrong reason — or, worse, a
        /// future rename could leave them asserting against an empty string.
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
        /// ⚠️ THE SILENT-LOSS GUARD (pipeline Run 1, debugger HIGH). <c>TypeInputViaXterm</c> returns
        /// without typing when the renderer is not initialized and tells the caller NOTHING. Because
        /// <c>Deliver</c> takes the exactly-once guard as its FIRST statement, a loss there is permanent:
        /// the question trigger and the 120s give-up are both already disarmed. So readiness must be
        /// awaited BEFORE the typing call, not merely checked somewhere in the method.
        /// <para>Ordering is asserted, not presence — a readiness wait placed AFTER the TypeInput would
        /// satisfy a contains-check while protecting nothing, which is the exact shape of the vacuous
        /// assertion this file already shipped once.</para>
        /// </summary>
        [Fact]
        public void Readiness_is_awaited_before_the_prompt_is_typed()
        {
            string body = DeliveryMethodBody();

            int wait = body.IndexOf("WaitForRendererReadyAsync", StringComparison.Ordinal);
            int type = body.IndexOf("TypeInput(oneLine", StringComparison.Ordinal);

            Assert.True(
                wait >= 0,
                "Deliver does not wait for renderer readiness. TypeInputViaXterm returns silently when "
                + "the renderer is not initialized, so at the ~5s trigger a slow WebView2 start drops the "
                + "job with the log still claiming it was delivered.");
            Assert.True(type >= 0, "The prompt is never typed at all.");
            Assert.True(
                wait < type,
                "Renderer readiness is awaited AFTER the prompt is typed. That is not a guard — the "
                + "typing it is supposed to protect has already happened.");
        }

        /// <summary>
        /// A delivery that fails after the guard is taken must still reach the spawner. Before this,
        /// every failure path inside <c>Deliver</c> logged and returned, so the spawner was told nothing
        /// and <c>GiveUp</c> — the only thing that writes <c>spawn_failed</c> — could never run, because
        /// the guard it checks was already set.
        /// </summary>
        [Fact]
        public void Every_delivery_failure_path_reports_to_the_spawner()
        {
            string body = DeliveryMethodBody();

            Assert.Contains("void ReportUndelivered", body, StringComparison.Ordinal);

            // GiveUp must delegate rather than carry its own copy of the notification code: two copies
            // would drift, and the inbox-routing rules in it were themselves the subject of two earlier
            // pipeline rounds.
            int giveUp = body.IndexOf("void GiveUp", StringComparison.Ordinal);
            Assert.True(giveUp >= 0, "GiveUp is gone — the 120s give-up no longer notifies anyone.");
            Assert.Contains(
                "ReportUndelivered(reason)",
                body[giveUp..],
                StringComparison.Ordinal);

            // The readiness failure is the path the debugger found; it must report, not just return.
            Assert.True(
                Regex.IsMatch(body, @"WaitForRendererReadyAsync[^;]*\)\s*\)\s*\{\s*ReportUndelivered", RegexOptions.Singleline),
                "The renderer-not-ready branch does not call ReportUndelivered. That is the silent "
                + "permanent job loss this fact exists to prevent.");
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
            Assert.DoesNotContain(
                "typeNextChar()",
                handlerBody);

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
