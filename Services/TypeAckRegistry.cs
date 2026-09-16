using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// How a <c>typeAck</c> / <c>typeAbort</c> was resolved. Returned rather than merely logged, for
    /// the reason <see cref="EnterAckOutcome"/> is: "the typing job completed promptly" is a claim
    /// that several different code paths can satisfy, so a result that cannot say WHICH path ran
    /// cannot prove the correlation works (task f420feeb's finding, applied here by task 8b270b37).
    /// </summary>
    internal enum TypeAckOutcome
    {
        /// <summary>Matched a waiting job by id. The only normal path; anything else is a signal.</summary>
        Correlated,

        /// <summary>
        /// An id arrived that no waiter holds — the job had already timed out and been released, or
        /// the page acked twice. Dropped.
        /// </summary>
        NoWaiter,

        /// <summary>
        /// The ack carried NO id. Dropped, never rescued — see the class remarks for why there is no
        /// compatibility path here even though <see cref="EnterAckRegistry"/> has one.
        /// </summary>
        Uncorrelated,
    }

    /// <summary>
    /// Correlates typing-job acknowledgments from <c>Terminal/terminal.html</c> with the
    /// <c>TypeInputViaXtermAsync</c> call that asked for them (task 8b270b37, checklist item 2).
    ///
    /// <para><b>⚠️ WHAT AN ACK MEANS, AND THE MISTAKE IT MUST NOT BE READ AS.</b> An ack says: every
    /// character of that job's payload, INCLUDING its trailing line ending, was handed to the
    /// terminal's input path. It says nothing whatever about what the terminal then did with them.
    /// In particular it does NOT mean a prompt was submitted — the failure this ticket exists to fix
    /// is a trailing CR that lands in Claude's composer as a newline, leaving the prompt typed but
    /// unsent. Deciding that is checklist item 3; this class is the plumbing underneath it.</para>
    ///
    /// <para><b>Why this is its own class, shaped like <see cref="EnterAckRegistry"/>.</b> Same
    /// reason: <c>WebViewTerminalRenderer</c> is a <c>UserControl</c> hosting WebView2 and cannot be
    /// instantiated in a test, so a decision left inside it can only ever be asserted by scanning its
    /// source. The decision — which waiter, if any, an ack releases — is pure, so it lives somewhere
    /// it can be exercised for real. The threading and locking rationale in
    /// <see cref="EnterAckRegistry"/> applies here verbatim and is not repeated: every mutation runs
    /// under <c>_gate</c>; callers are UI-thread-confined today by accident of every current caller,
    /// which is a trace and not a guarantee.</para>
    ///
    /// <para><b>⚠️ WHY THERE IS NO id-less COMPATIBILITY PATH, unlike
    /// <see cref="EnterAckRegistry"/>.</b> That class rescues an ack carrying no id, because a
    /// <c>terminal.html</c> predating task f420feeb's echo still SENDS <c>enterAck</c> — just without
    /// the field — and refusing it would time out every Enter and escalate into the OS-level
    /// <c>SendInput</c> rung. Nothing analogous exists here: no page has ever sent <c>typeAck</c> at
    /// all, so an id-less one has no benign origin. It can only mean the echo is broken, which is
    /// precisely the condition <see cref="EnterAckRegistry"/>'s own remarks warn would go undetected
    /// if it were silently rescued. So it is dropped and reported as
    /// <see cref="TypeAckOutcome.Uncorrelated"/>, and the caller logs it loudly.</para>
    /// </summary>
    internal sealed class TypeAckRegistry
    {
        // StringComparer.Ordinal is STATED rather than left to the default, for the reason spelled
        // out at length in EnterAckRegistry: the id is minted here, carried to the page as text,
        // echoed back verbatim and looked up again, and a permissive comparison in front of an exact
        // lookup is a bug generator. Nothing on this path trims or case-folds the value.
        private readonly Dictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private int _counter;

        /// <summary>Waiters currently outstanding. Test/diagnostic surface.</summary>
        internal int OutstandingCount
        {
            get { lock (_gate) { return _waiters.Count; } }
        }

        /// <summary>
        /// The comparer the waiter table actually uses, exposed so a test can pin it STRUCTURALLY
        /// rather than by scanning this file for the name of a comparer. (A source scan for a
        /// forbidden comparer name goes red when someone writes that name in a refusal comment,
        /// which is this codebase's house style — see <see cref="EnterAckRegistry.KeyComparer"/>.)
        /// </summary>
        internal IEqualityComparer<string> KeyComparer => _waiters.Comparer;

        /// <summary>
        /// How long to wait for a typing job's ack before giving up on it.
        ///
        /// <para>Unlike an Enter, a typing job's duration is KNOWN in advance and unbounded by any
        /// fixed constant: it is one <paramref name="charDelayMs"/> per byte of payload, so the 2000
        /// characters of a spawned helper's job prompt at 5ms/char take ten seconds all by
        /// themselves. A fixed 3s budget would report false on every one of them.</para>
        ///
        /// <para><b>The slack is deliberately generous, and that direction is chosen on purpose.</b>
        /// A job also has to wait behind anything already in the page's queue, and the host cannot
        /// see that queue — so the budget cannot be tight without occasionally MANUFACTURING a
        /// failure report for a job that was merely queued. A false "typed" is impossible here (only
        /// the page's ack produces true); a false "not typed" is possible and would be actively
        /// misleading, which is the same defect class this ticket is fixing. So: double the predicted
        /// typing time, plus 30 seconds.</para>
        ///
        /// <para>⚠️ KNOWN CEILING: capped at 10 minutes, so a payload above roughly 30,000 bytes at
        /// 20ms/char could be reported as not-typed while the page is still typing it. Nothing routes
        /// payloads of that size through this path today (bulk text goes through
        /// <c>TerminalControl.InjectChunkedInputAsync</c>, which does not touch the typing queue), and
        /// an unbounded wait is worse than a bounded wrong answer.</para>
        /// <para>There is no separate floor constant: the flat 30s term IS the floor, and adding a
        /// <c>Math.Clamp</c> lower bound on top of it would be a bound that can never bind — the kind
        /// of unreachable guard that reads as a decision somebody made.</para>
        /// </summary>
        /// <param name="payloadBytes">
        /// UTF-8 byte count of the payload including its trailing line ending. Bytes, not characters:
        /// the page types one <c>atob</c> output byte per tick, so a multi-byte character costs
        /// several ticks.
        /// </param>
        /// <param name="charDelayMs">Per-character delay the page was asked to use.</param>
        internal static int AckBudgetMs(int payloadBytes, int charDelayMs)
        {
            long predicted = (long)Math.Max(0, payloadBytes) * Math.Max(1, charDelayMs);
            long budget = (predicted * 2) + 30_000;
            return (int)Math.Min(budget, 600_000);
        }

        /// <summary>
        /// Mints a job id and registers a waiter for it.
        /// <para>The id is a STRING even though it is minted from a counter, for the reason given in
        /// <see cref="EnterAckRegistry.Register"/>: the host's hand-rolled JSON reader consumes
        /// digits only, with no sign, so a wrapped counter would arrive as garbage. A string
        /// round-trips whatever the counter produces.</para>
        /// <para>Ids are unique per registry, not process-wide, and that is sufficient because a
        /// registry is never shared: this is instance state, the renderer holds one, and
        /// <c>TerminalControl</c> constructs one renderer per terminal. Two panes typing at once both
        /// mint "7" harmlessly, because each page can only post to its own renderer. ⚠️ It also means
        /// this registry must never be merged with the Enter one — a single id space shared by the
        /// two ack kinds would let an <c>enterAck</c> release a typing waiter.</para>
        /// </summary>
        /// <returns>The job id to send, and the task that completes when its ack arrives.</returns>
        internal (string JobId, Task<bool> Ack) Register()
        {
            // RunContinuationsAsynchronously: completion runs on the WebView2 message callback and the
            // waiter's continuation should not — which is also what makes completing under the lock
            // safe, since a continuation cannot re-enter this class while the lock is held.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                string jobId = (++_counter).ToString(CultureInfo.InvariantCulture);
                _waiters[jobId] = tcs;
                return (jobId, tcs.Task);
            }
        }

        /// <summary>
        /// Forgets a job whether or not it was ever acked. Called from the sender's <c>finally</c>, so
        /// a timed-out job does not accumulate for the life of the renderer.
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
        /// <param name="jobId">Id echoed by the page. Null/empty means the echo is broken.</param>
        /// <param name="typed">
        /// True from <c>typeAck</c> (the job's last character was sent), false from <c>typeAbort</c>
        /// (the page dropped the job mid-way). The false case exists so a dropped job reports at once
        /// instead of sitting out the whole <see cref="AckBudgetMs"/> budget to reach the same answer.
        /// </param>
        internal TypeAckOutcome Complete(string jobId, bool typed)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return TypeAckOutcome.Uncorrelated;
            }

            lock (_gate)
            {
                // An ack for a job that already timed out is DROPPED. Using it to release some later
                // caller's wait would be exactly the cross-talk EnterAckRegistry exists to remove,
                // reproduced one channel over.
                if (!_waiters.TryGetValue(jobId, out var waiter))
                {
                    return TypeAckOutcome.NoWaiter;
                }

                _waiters.Remove(jobId);
                waiter.TrySetResult(typed);
                return TypeAckOutcome.Correlated;
            }
        }
    }
}
