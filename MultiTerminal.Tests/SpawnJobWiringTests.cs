using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Pins how <c>MainForm.QueueInitialPromptDelivery</c> hands a spawned helper its job (task 8b270b37):
    /// it STORES the job for the helper to collect, pushes nothing, and reports a job nobody collected.
    /// Plus one cross-file fact about <c>Terminal/terminal.html</c>'s typing queue, which still serialises
    /// every other <c>typeInput</c> (the <c>initializing...</c> banner among them).
    ///
    /// <para><b>History these facts replace.</b> The job used to be typed into the pane, where a trailing
    /// CR could become a newline in the composer. It then went over the helper's channel, where a message
    /// sent before Claude Code started listening for channel messages was dropped while the channel server
    /// answered 200. Live, Lv1x2's job went out 41ms too early. Both paths logged success.</para>
    ///
    /// <para><b>Why a source census.</b> The method is private in a WinForms form that cannot be
    /// instantiated in a test. What it decides is tested behaviourally in <see cref="SpawnJobStoreTests"/>;
    /// what only a census can see is whether this method CALLS the store, pushes nothing, and routes the
    /// give-up through the store's claim.</para>
    ///
    /// <para><b>Scoped to the METHOD, comments stripped.</b> <c>MainForm</c> has unrelated
    /// <c>DeliverViaChannel</c> and <c>TypeInput</c> callers, and this method's own doc names both push
    /// paths while explaining why they were removed.</para>
    /// </summary>
    public class SpawnJobWiringTests
    {
        [Fact]
        public void The_method_body_was_actually_located()
        {
            string body = DeliveryMethodBody();

            Assert.True(
                body.Length > 900,
                $"Extracted only {body.Length} chars for QueueInitialPromptDelivery. The method was "
                + "renamed or the brace matching broke; every other fact in this file is now vacuous.");
        }

        /// <summary>
        /// ⚠️ The job is STORED and nothing in this method pushes it. The store call is asserted in the same
        /// fact as the absences: without it, a slicing bug returning the wrong region would satisfy every
        /// <c>DoesNotContain</c> and prove nothing.
        /// </summary>
        [Fact]
        public void The_job_is_stored_for_collection_and_never_pushed()
        {
            string body = DeliveryMethodBody();

            Assert.Contains("jobs.TryAdd(docId,", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DeliverViaChannel", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TypeInput", body, StringComparison.Ordinal);
            Assert.DoesNotContain("InjectInput", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TerminalRegistered", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// A job MT could not store is reported, not dropped: the spawner was told the job travels with the
        /// spawn. The report must be the first statement of the branch taken when <c>TryAdd</c> fails.
        /// </summary>
        [Fact]
        public void A_job_that_cannot_be_stored_is_reported()
        {
            string body = DeliveryMethodBody();

            Assert.Matches(
                new Regex(@"if\s*\(\s*!jobs\.TryAdd\(docId,[^;]*?\)\)\s*\{\s*ReportUndeliveredSpawnJob\(", RegexOptions.Singleline),
                body);
        }

        /// <summary>
        /// ⚠️ The give-up goes through the store's claim, keyed on the pane's DOCID, and reports only when
        /// the claim succeeds. Without the claim, a job the helper already collected would still produce a
        /// <c>spawn_failed</c>, which is a false failure, the mirror image of this ticket's false success.
        /// Keyed on the docId because a display name is not an identity here (task c28e6177).
        /// </summary>
        [Fact]
        public void The_give_up_reports_only_an_uncollected_job_claimed_by_docId()
        {
            string body = DeliveryMethodBody();

            Assert.Contains("giveUpMs", body, StringComparison.Ordinal);
            Assert.Matches(
                new Regex(@"if\s*\(\s*!jobs\.TryClaimGiveUpReport\(docId\)\s*\)\s*return;\s*ReportUndeliveredSpawnJob\(", RegexOptions.Singleline),
                body);
            Assert.DoesNotContain("TryClaimGiveUpReport(agentName", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ NO SUCCESS-SHAPED LOG IN THIS METHOD. The line that opened this ticket, and then the channel
        /// path's "accepted by its channel", both announced a delivery nothing had confirmed. Here the only
        /// Info line is the attempt ("outcome to follow"). The receipt is logged by the collection handler,
        /// because only a collection proves the job reached the helper.
        /// </summary>
        [Fact]
        public void The_method_logs_an_attempt_and_never_a_delivery()
        {
            string body = DeliveryMethodBody();

            var infos = Regex.Matches(body, @"_debugLogService\?\.Info\(([^;]*)\);", RegexOptions.Singleline);
            Assert.True(infos.Count >= 1, "The method logs no attempt at all.");
            foreach (Match info in infos)
            {
                Assert.Contains("outcome to follow", info.Groups[1].Value, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// The receipt handler is subscribed exactly once. Unsubscribed, a collected job leaves no trace in
        /// the log and a live check has nothing to read; subscribed twice, every job logs two receipts.
        /// </summary>
        [Fact]
        public void The_collection_receipt_is_subscribed_exactly_once()
        {
            string src = ReadMainFormStripped();

            Assert.Single(Regex.Matches(src, @"SpawnService\.Jobs\.Collected\s*\+=\s*OnSpawnJobCollected"));
            Assert.Contains("private void OnSpawnJobCollected(", src, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ CROSS-FILE, and the compiler checks none of it. The typing queue lives in
        /// <c>Terminal/terminal.html</c>; the C# side cannot observe it. Two concurrent typeInput
        /// payloads used to interleave their characters into one composer and submit two garbled
        /// prompts. The <c>initializing...</c> banner that starts a spawned helper's first turn still
        /// travels this path.
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
        /// MainForm.cs with block and line comments removed. Stripping is load-bearing: the method's own
        /// comments name the removed push paths, so an unstripped scan would fail on prose.
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
