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
    /// <c>MainForm.QueueInitialPromptDelivery</c> (task 7806024f), plus one cross-file fact about
    /// <c>Terminal/terminal.html</c>'s typing queue — the delivery path ends in that file, and a fact
    /// about it is meaningless anywhere else. Precedent: <c>BoardHudDoorwayTests</c> likewise binds a
    /// C#-named census class to a panel's HTML. Cohesion by defect, not by file.
    ///
    /// <para>⚠️ SCOPE OF THE QUEUE FACT, because the obvious reading is too generous: it observes the
    /// <c>typeInput</c>/xterm path ONLY. <c>TerminalControl.InjectInputAsync</c> writes STRAIGHT to
    /// ConPTY and never reaches the queue, so no assertion here can see that path and none should be
    /// read as covering it. Unifying the two injection mechanisms is its own ticket.</para>
    ///
    /// <para>That scope is UNCHANGED — but the gap it describes is no longer unwatched. Task f420feeb
    /// found the ticket's own list of callers of the unserialized path was wrong (four listed, six
    /// live) and that nothing anywhere forced it to stay true. <c>InjectionPathCensusTests</c> now
    /// enumerates those callers and pins the path's shape, and <c>EnterAckCorrelationTests</c> covers
    /// the Enter-acknowledgment half. Read the three together; each is honest about what it cannot
    /// see, which is the only reason the set is worth anything.</para>
    ///
    /// <para><b>Why a source census.</b> <c>QueueInitialPromptDelivery</c> is private in an 8K+ LOC
    /// WinForms file that cannot be instantiated in a test. The DECISION it makes was extracted into
    /// <see cref="MultiTerminal.Services.HelperReadinessTrigger"/> and is unit-tested properly; what
    /// cannot be reached any other way is whether that decision is actually CALLED, and whether the
    /// handler is removed again. This is the same technique
    /// <c>AgentActivityObservationTests.MainForm_constructs_starts_and_disposes_the_activity_watcher</c>
    /// already uses against this file.</para>
    ///
    /// <para><b>Since task 8b270b37 item 5 it also pins what <c>Deliver</c> DOES with the submission
    /// oracle's answer</b> — the <c>switch (check.Verdict)</c> arms. Same technique and same reason:
    /// <c>ComposerOracle</c> decides, and is unit-tested properly in <c>ComposerOracleTests</c>, but
    /// the routing of its three verdicts to a log line or an inbox message is inline in this private
    /// method and reachable no other way. Those facts are sliced to a single switch ARM, not to the
    /// method, because two of them are negative and a method-wide scan would be satisfied by the
    /// other arms.</para>
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
        /// The typing call inside <c>Deliver</c>, as it is spelled in <c>MainForm.cs</c>. Two ordering
        /// facts below locate it, and it has now been renamed twice (<c>TypeInput</c> →
        /// <c>TypeInputAsync</c> → <c>TypeInputAndConfirmSubmissionAsync</c>, task 8b270b37), so it is
        /// named once here rather than transcribed into each of them.
        /// <para>⚠️ A rename makes both facts fail LOUDLY on "the prompt is never typed at all" —
        /// which is the correct behaviour and must stay that way. Do not soften this to a regex that
        /// matches any <c>TypeInput*</c>: what the facts are ordering against is the specific call
        /// that types the job, and a looser needle would happily match some future second typing site
        /// that the readiness wait does not protect.</para>
        /// </summary>
        private const string TypingCall = "TypeInputAndConfirmSubmissionAsync(oneLine";

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
        /// ⚠️ THE SILENT-LOSS GUARD (pipeline Run 1, debugger HIGH). <c>TypeInputViaXtermAsync</c>
        /// returns without typing when the renderer is not initialized. Because <c>Deliver</c> takes
        /// the exactly-once guard as its FIRST statement, a loss there is permanent: the question
        /// trigger and the 120s give-up are both already disarmed. So readiness must be awaited
        /// BEFORE the typing call, not merely checked somewhere in the method.
        /// <para>⚠️ TASK 8b270b37 DID NOT MAKE THIS WAIT REDUNDANT, and the tempting reading is the
        /// wrong one. Item 2 made that method return false instead of telling the caller nothing, and
        /// item 4 made <c>Deliver</c> consume the answer — so a loss here is no longer SILENT. It is
        /// still a loss. Without this wait, a slow WebView2 start turns a deliverable job into a
        /// <c>spawn_failed</c> report; the wait is what makes it a delivery instead. Reporting the
        /// failure accurately was never the goal — not having one was.</para>
        /// <para>Ordering is asserted, not presence — a readiness wait placed AFTER the TypeInput would
        /// satisfy a contains-check while protecting nothing, which is the exact shape of the vacuous
        /// assertion this file already shipped once.</para>
        /// </summary>
        [Fact]
        public void Readiness_is_awaited_before_the_prompt_is_typed()
        {
            string body = DeliveryMethodBody();

            int wait = body.IndexOf("WaitForRendererReadyAsync", StringComparison.Ordinal);
            int type = body.IndexOf(TypingCall, StringComparison.Ordinal);

            Assert.True(
                wait >= 0,
                "Deliver does not wait for renderer readiness. TypeInputViaXtermAsync returns false without "
                + "typing when the renderer is not initialized, and Deliver discards that result, so at the "
                + "~5s trigger a slow WebView2 start drops the job with the log still claiming it was delivered.");
            Assert.True(type >= 0, "The prompt is never typed at all.");
            Assert.True(
                wait < type,
                "Renderer readiness is awaited AFTER the prompt is typed. That is not a guard — the "
                + "typing it is supposed to protect has already happened.");
        }

        /// <summary>
        /// ⚠️ WHY A WRONG TRIGGER IS UNRECOVERABLE (task <c>c28e6177</c>). <c>Deliver</c> takes the
        /// exactly-once guard as its FIRST statement and immediately unsubscribes both other triggers —
        /// before the document is found, before readiness is awaited, before anything is typed.
        ///
        /// <para>That ordering is CORRECT and must stay: exactly-once is the whole point, and a guard
        /// taken later would let two triggers both type into the pane. It is pinned here because it is
        /// also what sets the price of firing on the wrong evidence. A delivery triggered by the wrong
        /// broker row cannot be retracted, retried, or noticed — the question trigger and the 120s
        /// give-up are already gone by the time the mistake is knowable. That is what makes the
        /// readiness predicate's correctness load-bearing rather than merely tidy, and it is the reason
        /// <c>c28e6177</c> re-keys that predicate instead of widening it.</para>
        ///
        /// <para>Only the FIRST unsubscribe is located: <c>GiveUp</c> carries the matching pair later in
        /// the same method, and <see cref="Both_exit_paths_unsubscribe_the_trigger"/> owns that fact.</para>
        /// </summary>
        [Fact]
        public void The_exactly_once_guard_is_taken_before_any_trigger_is_disarmed_or_anything_is_typed()
        {
            string body = DeliveryMethodBody();

            int guard = body.IndexOf("Interlocked.Exchange(ref delivered", StringComparison.Ordinal);
            int unsubscribe = body.IndexOf("TerminalRegistered -= onRegistered", StringComparison.Ordinal);
            int type = body.IndexOf(TypingCall, StringComparison.Ordinal);

            Assert.True(
                guard >= 0,
                "Deliver no longer takes an exactly-once guard. Two triggers can now both type into the "
                + "pane, which garbles the prompt rather than merely duplicating it.");
            Assert.True(unsubscribe >= 0, "Deliver does not unsubscribe the registration trigger.");
            Assert.True(type >= 0, "The prompt is never typed at all.");

            Assert.True(
                guard < unsubscribe,
                "The guard is taken AFTER the handlers are removed. Two concurrent triggers could both "
                + "pass the removal and both proceed to type.");
            Assert.True(
                guard < type,
                "The guard is taken AFTER the typing. Exactly-once no longer protects the thing it "
                + "exists to protect.");
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

        // ────────────────────────────────────── what each submission verdict is DONE with (8b270b37) ──

        /// <summary>
        /// The three arms of <c>switch (check.Verdict)</c> inside <c>Deliver</c>, in the order the
        /// method writes them. Named once because four facts below slice on them and a transcribed
        /// label drifts.
        /// </summary>
        private static readonly string[] OutcomeArmLabels =
        {
            "case SubmissionVerdict.Confirmed:",
            "case SubmissionVerdict.NotConfirmed:",
            "default:",
        };

        /// <summary>
        /// The vacuity guard for the three facts below — stated at the strength the falsification runs
        /// actually support, which is less than the obvious wording would claim.
        ///
        /// <para><b>⚠️ IT IS A DIAGNOSTIC, NOT AN INDEPENDENT GUARD.</b> Breaking the extraction
        /// (renaming the switch expression so <see cref="OutcomeSwitchBody"/>'s needle is gone) does
        /// turn this fact red — and turns the other three red in the same run, because all four share
        /// the extractor and its asserts. It has NOT been shown to fail on its own. What it buys is
        /// attribution: when four facts go red together, this one says the cause is the slicing rather
        /// than the routing.</para>
        ///
        /// <para><b>⚠️ AND THE LENGTH FLOORS DO NOT CATCH AN EMPTIED ARM.</b> Deleting the Confirmed
        /// arm's only statement was run. The slice still measured well over the floor, this fact
        /// stayed GREEN, and the red came from <see cref="The_unknown_arm_is_not_logged_as_a_success"/>
        /// instead. The floors catch an extraction that collapsed to nothing, which is what they are
        /// for; do not read them as checking that an arm still does anything.</para>
        /// </summary>
        [Fact]
        public void The_outcome_switch_and_all_three_of_its_arms_were_actually_located()
        {
            string sw = OutcomeSwitchBody();

            Assert.True(
                sw.Length > 400,
                $"Extracted only {sw.Length} chars for the verdict switch; every outcome fact below is now vacuous.");

            foreach (string label in OutcomeArmLabels)
            {
                string arm = OutcomeArm(label);
                Assert.True(
                    arm.Length > 80,
                    $"The '{label}' arm sliced down to {arm.Length} chars, so the slicing has collapsed and "
                    + "the facts below are no longer reading the region they name. (This floor does NOT "
                    + "detect an arm whose statements were deleted — that stays well above it.)");
            }
        }

        /// <summary>
        /// ⚠️ NotConfirmed MUST RELAY THE ORACLE'S OWN ADVICE. <c>ReportUndelivered</c>'s default
        /// disposition — "Nothing was typed into the pane." — is FALSE for this caller: the text is in
        /// that composer, which is the entire finding. It is written into a <c>spawn_failed</c> inbox
        /// message that an agent holding an undelivered job reads, and the obvious response to
        /// "nothing was typed" is to type it again, onto a composer that already contains it.
        ///
        /// <para>Asserted positively (the arm passes <c>check.Advice</c>) rather than by checking the
        /// default sentence is absent. The negative form is the direction that MANUFACTURES failures:
        /// this codebase's house style is to explain in a comment above the code why the other option
        /// was rejected, and that comment names the sentence verbatim — as the one in this very arm
        /// does today.</para>
        /// </summary>
        [Fact]
        public void The_not_confirmed_arm_relays_the_oracles_advice_to_the_spawner()
        {
            string arm = OutcomeArm("case SubmissionVerdict.NotConfirmed:");

            Assert.Contains("ReportUndelivered(", arm, StringComparison.Ordinal);
            Assert.True(
                Regex.IsMatch(arm, @"disposition:\s*check\.Advice"),
                "The NotConfirmed arm no longer passes check.Advice as the disposition, so the report "
                + "falls back to \"Nothing was typed into the pane.\" — which is false for this caller, "
                + "and is read by an agent whose response to it is to retype the job.");
        }

        /// <summary>
        /// ⚠️ Unknown MUST NOT FILE A <c>spawn_failed</c>. Unknown means the check could not run — no
        /// boxed composer, no probe function, too little distinctive text. Reporting that as a
        /// delivery failure manufactures failures out of the detector's own blind spots, which is the
        /// defect class this ticket removes rather than a new instance of it.
        ///
        /// <para>The NotConfirmed arm is asserted to CONTAIN the call in the same fact. That is the
        /// discriminator, not decoration: without it, a slicing bug that returned the wrong region
        /// would satisfy the <c>DoesNotContain</c> and this fact would pass while proving nothing.</para>
        /// </summary>
        [Fact]
        public void The_unknown_arm_files_no_spawn_failed()
        {
            Assert.Contains(
                "ReportUndelivered",
                OutcomeArm("case SubmissionVerdict.NotConfirmed:"),
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                "ReportUndelivered",
                OutcomeArm("default:"),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠️ THE OTHER DIRECTION, AND THE ONE THAT IS THE ORIGINAL BUG. Unknown must not be logged as
        /// a success either. The whole ticket exists because a prompt sitting unsent in a composer read,
        /// in the log, exactly like a prompt that had been answered — so an Info line on the one path
        /// that admits it does not know is that defect restored under a new name.
        ///
        /// <para>"A success log" is pinned as <c>Info</c> specifically, because the Confirmed arm is
        /// asserted here to be the thing that uses it: the two arms must not be indistinguishable to
        /// someone reading the log. The Unknown arm must still say something — silence there is its own
        /// failure — so <c>Warning</c> is required to be present, not merely <c>Info</c> absent.</para>
        /// </summary>
        [Fact]
        public void The_unknown_arm_is_not_logged_as_a_success()
        {
            Assert.Contains(
                "?.Info(",
                OutcomeArm("case SubmissionVerdict.Confirmed:"),
                StringComparison.Ordinal);

            string unknown = OutcomeArm("default:");

            Assert.DoesNotContain("?.Info(", unknown, StringComparison.Ordinal);
            Assert.Contains("?.Warning(", unknown, StringComparison.Ordinal);
        }

        /// <summary>
        /// The <c>switch (check.Verdict)</c> block inside <c>Deliver</c>, comments already stripped by
        /// <see cref="DeliveryMethodBody"/>, located by brace matching.
        /// <para>Stripping is load-bearing here in BOTH directions: the arms are documented with the
        /// identifiers these facts look for (an unstripped <c>Contains</c> would pass on prose alone),
        /// and the Unknown arm's comment explains at length why it does NOT file a failure report (an
        /// unstripped <c>DoesNotContain</c> would be tripped by that explanation). The known residual
        /// is the one already recorded on <see cref="StripComments"/>: a TRAILING comment on a line of
        /// real code survives.</para>
        /// </summary>
        private static string OutcomeSwitchBody()
        {
            string body = DeliveryMethodBody();

            int start = body.IndexOf("switch (check.Verdict)", StringComparison.Ordinal);
            Assert.True(
                start >= 0,
                "Deliver no longer switches on check.Verdict. Either the oracle's answer is being "
                + "discarded again, or it is consumed somewhere these facts cannot see.");

            int open = body.IndexOf('{', start);
            Assert.True(open >= 0, "No opening brace after the verdict switch.");

            int depth = 0;
            for (int i = open; i < body.Length; i++)
            {
                if (body[i] == '{') depth++;
                else if (body[i] == '}')
                {
                    depth--;
                    if (depth == 0) return body[open..(i + 1)];
                }
            }

            Assert.Fail("Braces never balanced for the verdict switch.");
            return string.Empty;
        }

        /// <summary>One arm of the verdict switch: from its label to the next label, or to the end.</summary>
        private static string OutcomeArm(string label)
        {
            string sw = OutcomeSwitchBody();

            int start = sw.IndexOf(label, StringComparison.Ordinal);
            Assert.True(start >= 0, $"The verdict switch has no '{label}' arm.");

            int end = sw.Length;
            foreach (string other in OutcomeArmLabels)
            {
                if (string.Equals(other, label, StringComparison.Ordinal)) continue;

                int at = sw.IndexOf(other, StringComparison.Ordinal);
                if (at > start && at < end) end = at;
            }

            return sw[start..end];
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
