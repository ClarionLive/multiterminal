using System;
using System.Collections.Concurrent;
using System.Globalization;
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
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.Ordinal);
        private int _counter;

        /// <summary>Waiters currently outstanding. Test/diagnostic surface.</summary>
        internal int OutstandingCount => _waiters.Count;

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
            string jobId = Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);

            // RunContinuationsAsynchronously: the completion runs on the WebView2 message callback,
            // and the waiter's continuation should not.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters[jobId] = tcs;
            return (jobId, tcs.Task);
        }

        /// <summary>
        /// Forgets a job whether or not it was ever acked. Called from the sender's <c>finally</c>:
        /// a failed injection makes up to five attempts and the renderer outlives all of them.
        /// </summary>
        internal void Release(string jobId)
        {
            if (!string.IsNullOrEmpty(jobId))
            {
                _waiters.TryRemove(jobId, out _);
            }
        }

        /// <summary>
        /// Resolves one acknowledgment and reports which path it took.
        /// </summary>
        /// <param name="jobId">Id echoed by the page, or null/empty from a page predating the echo.</param>
        internal EnterAckOutcome Complete(string jobId)
        {
            if (!string.IsNullOrEmpty(jobId))
            {
                // An ack for a job that already timed out is DROPPED. Using it to release some later
                // caller's wait would be exactly the cross-talk this class exists to remove.
                return _waiters.TryRemove(jobId, out var waiter) && waiter.TrySetResult(true)
                    ? EnterAckOutcome.Correlated
                    : EnterAckOutcome.NoWaiter;
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
            if (_waiters.Count == 1)
            {
                foreach (var pair in _waiters)
                {
                    if (_waiters.TryRemove(pair.Key, out var only))
                    {
                        only.TrySetResult(true);
                        return EnterAckOutcome.CompatibilitySingleWaiter;
                    }

                    break;
                }

                // The single waiter was removed by its own timeout between the count and the take.
                return EnterAckOutcome.NoWaiter;
            }

            return EnterAckOutcome.AmbiguousDropped;
        }
    }
}
