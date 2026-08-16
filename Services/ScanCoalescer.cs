#nullable enable

using System;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Coalesces repeated invocations of one expensive, side-effect-free scan
    /// (task 36b0b9d5 item ②, from 1ce9ddaf's Run-1 Codex security MEDIUM, which
    /// the cross-model adversary independently raised as the same concern).
    /// </summary>
    /// <remarks>
    /// THE PROBLEM. <c>POST /api/session-lineage/register</c> kicks off the janitor
    /// scans and, on budget overrun, ships the response while letting the scan run on.
    /// With no dedup or concurrency cap, repeated registers pile up concurrent
    /// system-wide scans — each enumerating registered projects and spawning
    /// <c>git for-each-ref</c> per distinct repo with GitExec timeouts up to 30s.
    /// Task 1ce9ddaf made this worse in one specific way: it removed the client-visible
    /// 15s backpressure that had been throttling retry cadence. Any caller able to POST
    /// a syntactically valid register could multiply background work.
    ///
    /// WHY COALESCING RATHER THAN CANCELLATION. Cancellation bounds ONE scan; it does
    /// not stop N callers starting N scans. Single-flight makes concurrent callers
    /// SHARE a scan, which attacks the amplification directly, and it introduces no
    /// staleness beyond the shared run's own duration — a joined result was computed
    /// concurrently with the request, not before it.
    ///
    /// TWO SEPARATE GUARANTEES, deliberately not conflated:
    ///
    ///   SINGLE-FLIGHT is unconditional. At most one run is in flight at a time. This is
    ///   the property that caps resource use, so it is not opt-in and cannot be switched off.
    ///
    ///   FRESHNESS is opt-in per call (<c>maxStalenessMs</c>, default 0 = strictest).
    ///   It governs BOTH forms of reuse — see the next paragraph, which is the part that
    ///   was wrong in the first cut of this class.
    ///
    /// STALENESS HAS TWO SOURCES, NOT ONE (task 36b0b9d5 pipeline Run 1, Codex adversary
    /// MEDIUM). The obvious one is reusing a COMPLETED result. The one the first cut
    /// missed is JOINING AN IN-FLIGHT RUN: a run that started before the caller arrived
    /// snapshotted state the caller may specifically be asking about. The original code
    /// returned <c>_inFlight</c> unconditionally, so <c>GET /api/worktrees/pending-merges</c> —
    /// documented as the explicit-refresh path and passing 0 — could join a scan that began
    /// before the very task completion it was invoked to reveal, and answer
    /// <c>status: ok, count: 0</c>. A confident wrong answer, which is the failure class
    /// this whole line of work exists to prevent. Both sources are now governed by the
    /// SAME <c>maxStalenessMs</c>: a caller joins an in-flight run only if that run STARTED
    /// within its staleness allowance, and a caller passing 0 never joins one.
    ///
    /// A caller that cannot join does NOT start a competing run — it attaches to a single
    /// shared FOLLOW-UP that is promoted the moment the current run finishes. So the
    /// resource bound survives the freshness fix: at most one run executing, plus at most
    /// one queued behind it, no matter how many callers arrive.
    ///
    /// FALSIFIABILITY. The cached value is the scan RESULT OBJECT, carried whole. For the
    /// janitor scans that means their <c>Complete</c> / <c>SkippedRecords</c> /
    /// <c>SkippedTaskIds</c> ride along unmodified, so a reused PARTIAL scan still reads
    /// as partial. Serving a cached partial as fresh-and-clean would silently break the
    /// "couldn't look ≠ found none" contract task 1ce9ddaf worked to preserve. This class
    /// must never synthesise, merge, or normalise a result.
    ///
    /// FAILURES ARE NOT CACHED. A faulted run propagates to everyone sharing it and is
    /// not remembered, so the next caller retries rather than inheriting a stale failure.
    /// </remarks>
    /// <typeparam name="T">
    /// The scan result. Reference type — the cache stores it BY REFERENCE, so the same
    /// instance is handed to every joiner and to every TTL reuser. Callers must therefore
    /// treat a returned result as READ-ONLY; mutating it mutates what other callers see.
    /// </typeparam>
    internal sealed class ScanCoalescer<T>
        where T : class
    {
        private readonly object _sync = new object();

        /// <summary>The run currently executing, or null when idle.</summary>
        private Task<T>? _inFlight;

        /// <summary>
        /// <see cref="Environment.TickCount64"/> when <see cref="_inFlight"/> STARTED.
        /// Load-bearing for freshness: a caller may join that run only if it began within
        /// the caller's staleness allowance, because the run's snapshot is as old as its start.
        /// </summary>
        private long _inFlightStartedMs;

        /// <summary>
        /// The single shared follow-up run, promoted when <see cref="_inFlight"/> finishes.
        /// Callers too strict to join the current run attach here instead of starting a
        /// competing one — that is what keeps the freshness fix from costing the resource bound.
        /// </summary>
        private TaskCompletionSource<T>? _queuedNext;

        /// <summary>Factory the queued follow-up will run (the first strict caller's).</summary>
        private Func<Task<T>>? _queuedFactory;

        /// <summary>Last successful result, eligible for TTL reuse. Never a failure.</summary>
        private T? _last;

        /// <summary><see cref="Environment.TickCount64"/> when <see cref="_last"/> landed.</summary>
        private long _lastCompletedMs;

        /// <summary>
        /// Run <paramref name="factory"/>, or join an equivalent run already under way,
        /// or reuse a recent result when <paramref name="maxStalenessMs"/> allows it.
        /// </summary>
        /// <param name="factory">
        /// Produces the scan. May do real work SYNCHRONOUSLY before its first await —
        /// it is dispatched through <see cref="Task.Run{TResult}(Func{Task{TResult}})"/>
        /// so that prologue never runs while this instance's lock is held (the same
        /// sync-pre-await trap item ① fixed in the register budget).
        /// </param>
        /// <param name="maxStalenessMs">
        /// How old the data may be. Governs BOTH reuse of a completed result AND joining a
        /// run already in flight (a run's snapshot is as old as its start). 0 — the default,
        /// and what the explicit REST refresh endpoints pass — means neither: the caller is
        /// guaranteed a run that begins after it arrived. It still never starts a competing
        /// run; it queues behind the current one.
        /// </param>
        public Task<T> RunAsync(Func<Task<T>> factory, int maxStalenessMs = 0)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_sync)
            {
                long now = Environment.TickCount64;

                if (maxStalenessMs > 0
                    && _last != null
                    && now - _lastCompletedMs <= maxStalenessMs)
                {
                    return Task.FromResult(_last);
                }

                if (_inFlight != null)
                {
                    // Join only a run whose SNAPSHOT is fresh enough for this caller. With
                    // maxStalenessMs == 0 this is never true, which is the whole point:
                    // an explicit-refresh caller must not be handed a scan that started
                    // before the state change it was invoked to observe.
                    if (maxStalenessMs > 0 && now - _inFlightStartedMs <= maxStalenessMs)
                    {
                        return _inFlight;
                    }

                    // Too stale to join — attach to the ONE shared follow-up instead of
                    // starting a competing run. Promoted by CompleteAsync under the lock,
                    // so there is no window where a queued run exists but nothing is in flight.
                    if (_queuedNext == null)
                    {
                        _queuedNext = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _queuedFactory = factory;
                    }

                    return _queuedNext.Task;
                }

                // Publish the handle BEFORE starting the work, so completion can never
                // outrun publication. The race being closed: if the handle were the
                // worker's own task and that task were already complete at assignment
                // time, its "clear _inFlight when done" step would run FIRST (the lock
                // is reentrant, so it would not even block), and the assignment would
                // then pin a finished task there forever — wedging every later scan
                // into silently returning the first result.
                //
                // HONESTY ABOUT THIS DEFENCE, so nobody later mistakes it for load-bearing
                // and nobody deletes it as pointless: it is NOT demonstrated by any test
                // here. The experiment was run — swapping in the worker's-own-task form
                // while keeping the Task.Run below passes all of ScanCoalescerTests,
                // because a freshly scheduled Task.Run is essentially never complete by
                // the time the assignment executes. The window is real but not reachable
                // on demand, which is exactly why a test cannot pin it. Kept because the
                // TCS closes it BY CONSTRUCTION at negligible cost, not because a red
                // test forced it.
                //
                // The Task.Run below IS load-bearing and IS demonstrated: removing it
                // makes the factory's synchronous prologue run under this lock, and
                // SynchronousPrologue_DoesNotRunUnderTheLock deadlocks.
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight = tcs.Task;
                _inFlightStartedMs = now;

                // Runs synchronously only as far as the Task.Run dispatch inside —
                // no scan work happens under the lock.
                _ = CompleteAsync(tcs, factory);

                return tcs.Task;
            }
        }

        private async Task CompleteAsync(TaskCompletionSource<T> tcs, Func<Task<T>> factory)
        {
            TaskCompletionSource<T>? promoted;
            Func<Task<T>>? promotedFactory;
            T result;

            try
            {
                result = await Task.Run(factory).ConfigureAwait(false);

                // Record and release BEFORE completing the TCS: a waiter woken by
                // SetResult must never observe a state where the run is still
                // advertised as in flight.
                lock (_sync)
                {
                    _last = result;
                    _lastCompletedMs = Environment.TickCount64;
                    promoted = PromoteQueuedLocked(out promotedFactory);
                }
            }
            catch (Exception ex)
            {
                // Deliberately does NOT touch _last — a failure is not a result, and
                // the next caller must be free to retry rather than inherit it.
                lock (_sync)
                {
                    promoted = PromoteQueuedLocked(out promotedFactory);
                }

                // Completing OUTSIDE the try, with Try* (task 36b0b9d5 pipeline Run 2,
                // debugger hardening note). If completion sat inside the try and ever threw
                // — only reachable via double-completion, which a fresh TCS makes impossible
                // today — the catch would run PromoteQueuedLocked a SECOND time, nulling
                // _inFlight after the slot was already handed to the follow-up and discarding
                // the promoted reference. The queued run would then never start and never
                // complete: its waiters hang forever while the coalescer advertises itself
                // as idle. Cheaper to make that shape unreachable by construction than to
                // rely on it staying unreachable.
                tcs.TrySetException(ex);
                StartFollowUp(promoted, promotedFactory);
                return;
            }

            tcs.TrySetResult(result);
            StartFollowUp(promoted, promotedFactory);
        }

        /// <summary>
        /// Kick off the promoted follow-up, outside the lock and after the finished run's
        /// waiters have been completed. A queued run inherits nothing from its predecessor —
        /// in particular a failed predecessor does not fail it.
        /// </summary>
        private void StartFollowUp(TaskCompletionSource<T>? promoted, Func<Task<T>>? factory)
        {
            if (promoted != null && factory != null)
            {
                _ = CompleteAsync(promoted, factory);
            }
        }

        /// <summary>
        /// Hand the in-flight slot to the queued follow-up, or clear it when nothing is
        /// waiting. Must be called while holding <see cref="_sync"/>: doing the clear and
        /// the promote in one atomic step is what prevents a third caller from seeing an
        /// idle coalescer and starting a competing run while a follow-up is pending.
        /// </summary>
        private TaskCompletionSource<T>? PromoteQueuedLocked(out Func<Task<T>>? factory)
        {
            var queued = _queuedNext;
            factory = _queuedFactory;
            _queuedNext = null;
            _queuedFactory = null;

            if (queued != null)
            {
                _inFlight = queued.Task;
                _inFlightStartedMs = Environment.TickCount64;
            }
            else
            {
                _inFlight = null;
            }

            return queued;
        }
    }
}
