using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// How an <c>enterAck</c> was resolved. Returned rather than merely logged, because which path
    /// the ack took is the fact the tests assert on — "the Enter completed promptly" is satisfied by
    /// the correlated path AND by the compatibility path, so a result that cannot tell them apart
    /// cannot prove the correlation works (task f420feeb).
    /// </summary>
    internal enum EnterAckOutcome
    {
        /// <summary>Matched a waiting job by id. The normal path; anything else is a signal.</summary>
        Correlated,

        /// <summary>No waiter held that id — the attempt had already timed out. Dropped.</summary>
        NoWaiter,

        /// <summary>No id on the ack and exactly one waiter outstanding: the compatibility path.</summary>
        CompatibilitySingleWaiter,

        /// <summary>No id and two or more waiters outstanding. Dropped rather than guessed.</summary>
        AmbiguousDropped,
    }

    /// <summary>
    /// Correlates Enter-key acknowledgments with the injections that asked for them.
    ///
    /// <para><b>Why this is its own class.</b> It lives in <c>WebViewTerminalRenderer</c>'s problem
    /// space but not in its file, for the reason <see cref="HelperReadinessTrigger"/> exists: the
    /// renderer is a <c>UserControl</c> hosting WebView2 and cannot be instantiated in a test, so a
    /// decision left inside it can only ever be asserted by scanning its source. The DECISION —
    /// which waiter, if any, a given ack releases — is pure, so it moves somewhere it can be
    /// exercised for real.</para>
    ///
    /// <para><b>What it replaced.</b> A single <c>TaskCompletionSource</c> field shared by every
    /// injection into a terminal. Callers of <c>InjectInputAsync</c> are <c>async void</c> handlers
    /// on the UI thread, so each await pumps the message loop and lets the next injection start;
    /// two overlapping injections then shared one field. That produced cross-talk (B's ack completed
    /// A's wait) and, because the old code re-read the field for <c>.Result</c> after awaiting it, a
    /// blocking wait on an incomplete TaskCompletionSource on the UI thread.</para>
    ///
    /// <para><b>⚠️ THREADING — THIS CLASS ASSUMES NOTHING ABOUT ITS CALLERS, AND THAT IS THE POINT.</b>
    /// Every mutation runs under <c>_gate</c>. Read that as deliberate rather than defensive, because
    /// the honest position is uncomfortable: <b>the lock is belt-and-braces TODAY and load-bearing the
    /// moment anyone calls in from a pool thread</b>, and nothing anywhere makes today's situation a
    /// rule.</para>
    ///
    /// <para>Every caller is currently confined to the UI thread. <c>Complete</c> is reached from
    /// <c>WebViewTerminalRenderer.OnWebMessageReceived</c>, a WebView2 event with UI-thread affinity by
    /// contract; <c>Register</c> and <c>Release</c> from <c>TrySendEnterViaJsAsync</c>, whose awaits
    /// resume through <c>WindowsFormsSynchronizationContext</c>. Upstream, every caller of
    /// <c>InjectInputAsync</c> marshals before reaching it — the six are enumerated in
    /// <c>InjectionPathCensusTests.Every_caller_of_the_unserialized_injection_path_is_enumerated_here</c>,
    /// so the trace can be RE-RUN rather than re-derived. The least obvious one: <c>/new-project</c>
    /// hangs off <c>ClaudeCodeDetected</c>, raised at <c>TerminalControl.cs:946</c> INSIDE
    /// <c>OnTerminalDataReceived</c>, which marshals at its own top — so even that path starts on the
    /// UI thread.</para>
    ///
    /// <para><b>⚠️ THAT IS TRACED, NOT ENFORCED, AND NOT PROVEN.</b> It is an accident of every caller
    /// that exists, not a property of this class — there is no attribute, no assert and no test holding
    /// it. Two of the six were re-checked independently (the broker-driven inject handler, likeliest to
    /// arrive on an HTTP/pool thread, and the <c>/new-project</c> path); the other four rest on one
    /// reading. A single future caller reaching this from a <c>Task.Run</c> continuation would make the
    /// interleaving live with NO compile error, no failing test, and one Enter of cross-talk as the only
    /// symptom. So: do not delete the lock on the grounds that everything is on the UI thread. That
    /// sentence is true and is not a guarantee.</para>
    ///
    /// <para><b>Why a lock rather than a concurrent collection.</b> This was a
    /// <c>ConcurrentDictionary</c> and the change reads like a step backwards, so the reason is written
    /// down: the compatibility branch in <see cref="Complete"/> must decide "is EXACTLY ONE waiter
    /// outstanding, and if so take it". That needs two operations to be atomic, and a concurrent
    /// collection offers no primitive for it — the previous code read <c>Count == 1</c> and then
    /// enumerated-and-removed, which is two steps wearing one. The lock is the fix, not a retreat from
    /// one.</para>
    ///
    /// <para><b>Honest severity of the defect that prompted this</b> (corrected after the fact): the
    /// non-atomic version was wrong IN THE CODE but unreachable BY ANY CURRENT CALLER, for the
    /// confinement reason above. Unreachable is not the same as correct, which is why the fix stayed —
    /// but the first commit describing it said the guarantee was "not held" without that qualifier, and
    /// overstated it.</para>
    /// </summary>
    internal sealed class EnterAckRegistry
    {
        // ⚠️ StringComparer.Ordinal IS STATED, not left to the default, because the default being
        // correct here is a fact nobody would re-derive. The id is minted here, carried to
        // terminal.html as text, echoed back verbatim, and looked up again — and a PERMISSIVE
        // comparison in front of an exact lookup is a bug generator, not a safety margin (the
        // general form of task c28e6177's finding, where a case-insensitive predicate approved a
        // delivery that the exact lookup behind it then failed to find). Every hop must compare the
        // same way. Nothing on this path trims or case-folds the VALUE: the page takes it with
        // substring, and the host's JSON reader lower-cases property NAMES only.
        private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.Ordinal);
        private int _counter;

        // ⚠️ A PLAIN LOCK, DELIBERATELY, REPLACING A ConcurrentDictionary (peer review of this ticket,
        // self-reported). The compatibility branch below has to decide "is EXACTLY ONE waiter
        // outstanding, and if so take it" — and a concurrent collection cannot do that, because the
        // count and the take are two operations. The previous version read `Count == 1` and then
        // enumerated-and-removed, so a Register() landing in between completed a waiter while TWO were
        // outstanding: precisely the "drop rather than guess" guarantee the comment claimed to hold,
        // not held. Worse, the dictionary enumerates by hash bucket, so the waiter taken could be the
        // NEWLY ADDED one rather than the one that was there at the check.
        //
        // Every operation here is a handful of instructions and happens once per Enter attempt, so
        // there is nothing to win by being lock-free and a correctness guarantee to lose. Completing a
        // TaskCompletionSource under the lock is safe BECAUSE Register creates them with
        // RunContinuationsAsynchronously: a waiter's continuation is scheduled, never run inline, so it
        // cannot re-enter this class while the lock is held.
        private readonly object _gate = new();

        /// <summary>Waiters currently outstanding. Test/diagnostic surface.</summary>
        internal int OutstandingCount
        {
            get { lock (_gate) { return _waiters.Count; } }
        }

        /// <summary>
        /// The comparer the waiter table actually uses, exposed so a test can pin it STRUCTURALLY
        /// rather than by scanning this file for the name of a comparer.
        /// <para>The textual version of that pin was a defect in its own right (found in peer review):
        /// a source scan for a FORBIDDEN comparer name goes RED when someone writes that name in a
        /// REFUSAL COMMENT — which is this codebase's house style, and which the comment above very
        /// nearly does. A false failure is worse here than a false pass: it punishes the documenting
        /// instinct and pressures the next reader to change correct code to make a test green. Both
        /// directions were demonstrated before this replaced it.</para>
        /// </summary>
        internal IEqualityComparer<string> KeyComparer => _waiters.Comparer;

        /// <summary>
        /// Mints a job id and registers a waiter for it.
        /// <para>The id is a STRING even though it is minted from a counter. The host's JSON reader
        /// (<c>WebViewTerminalRenderer.ParseJsonMessage</c>) is hand-rolled and its number branch
        /// consumes digits only — no sign — so a counter that has wrapped past <see cref="int.MaxValue"/>
        /// would arrive as garbage. A string round-trips whatever the counter produces.</para>
        /// </summary>
        /// <returns>The job id to send, and the task that completes when its ack arrives.</returns>
        internal (string JobId, Task<bool> Ack) Register()
        {
            // ⚠️ IDS ARE UNIQUE PER REGISTRY, NOT PROCESS-WIDE — and that is sufficient, because a
            // registry is never shared. The counter and the waiter table are both INSTANCE state
            // (nothing in this class is static); WebViewTerminalRenderer holds one registry as an
            // instance field; and TerminalControl constructs one renderer per terminal. So two
            // panes injecting at once both mint "7" and it is harmless: each page can only post to
            // its OWN renderer's WebView2, so an ack is matched inside the single registry that
            // issued the id. Ids are never compared across terminals and must not start being.
            // Sharing one registry between renderers WOULD reintroduce exactly the cross-talk this
            // class removes, one level down and invisible until two panes inject simultaneously.
            // RunContinuationsAsynchronously: the completion runs on the WebView2 message callback, and
            // the waiter's continuation should not — which is also what makes completing under the lock
            // safe (see _gate).
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                // Counter under the lock too, so the id and the table entry are minted as one step.
                // ⚠️ KNOWN, UNREACHABLE, AND NOT CODED AGAINST: after 2^31 injections into a single
                // pane the counter wraps, and the indexer would then OVERWRITE a live waiter rather
                // than refuse — orphaning it until its own 3s timeout. Noted rather than guarded
                // because the guard would be dead code at ~2.1 billion Enters in one renderer's
                // lifetime. Raised by Carol in peer review, who declined to report it as a finding for
                // that reason and mentioned it only because the doc below reasons about wrap for a
                // different consequence. Recorded here so the next reader gets the same courtesy.
                string jobId = (++_counter).ToString(CultureInfo.InvariantCulture);
                _waiters[jobId] = tcs;
                return (jobId, tcs.Task);
            }
        }

        /// <summary>
        /// Forgets a job whether or not it was ever acked. Called from the sender's <c>finally</c>:
        /// a failed injection makes up to five attempts and the renderer outlives all of them.
        /// </summary>
        internal void Release(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) return;

            lock (_gate)
            {
                _waiters.Remove(jobId);
            }
        }

        /// <summary>
        /// Resolves one acknowledgment and reports which path it took.
        /// </summary>
        /// <param name="jobId">Id echoed by the page, or null/empty from a page predating the echo.</param>
        internal EnterAckOutcome Complete(string jobId)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(jobId))
                {
                    // An ack for a job that already timed out is DROPPED. Using it to release some
                    // later caller's wait would be exactly the cross-talk this class exists to remove.
                    if (_waiters.TryGetValue(jobId, out var waiter))
                    {
                        _waiters.Remove(jobId);
                        waiter.TrySetResult(true);
                        return EnterAckOutcome.Correlated;
                    }

                    return EnterAckOutcome.NoWaiter;
                }

            // ── COMPATIBILITY PATH — see the removal condition below ────────────────────────────
            // An ack with no id can only come from a terminal.html predating the echo (a cached
            // page): the shipped pair always sends one. It is kept because without it every Enter
            // times out five times and escalates into the OS-level SendInput rung of the retry
            // ladder (task 9f599d7c) — a far worse failure than a brief ambiguity here.
            //
            // ⚠️ IT IS ALSO A TRAP, which is why the caller logs at WARNING every time it fires and
            // why the tests assert the correlated path positively. If the page's echo were broken —
            // a mistyped field, or an echo added to only one of the two ack sites — EVERY ack would
            // arrive id-less and land here, every Enter would complete promptly, and the suite would
            // stay green while the correlation it is protecting never ran once. Routine firing of
            // this path does not mean compatibility is working; it means the fix is not.
            //
            // REMOVAL CONDITION: delete this branch (and let a missing id fall through to
            // AmbiguousDropped) once no deployed build can still be serving a terminal.html without
            // the echo — in practice, one release after the Owner confirms the warning below has
            // stopped appearing in the debug log.
                // ⚠️ THE COUNT AND THE TAKE ARE ONE STEP, under the lock held since the top of this
                // method. They used to be two, and that made the guarantee below conditional on
                // nothing registering in between — see _gate for what that cost.
                if (_waiters.Count != 1)
                {
                    return EnterAckOutcome.AmbiguousDropped;
                }

                var single = _waiters.First();
                _waiters.Remove(single.Key);
                single.Value.TrySetResult(true);
                return EnterAckOutcome.CompatibilitySingleWaiter;
            }
        }
    }
}
