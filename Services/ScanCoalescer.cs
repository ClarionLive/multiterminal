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
    /// staleness at all — a joined result was computed concurrently with the request.
    ///
    /// TWO SEPARATE GUARANTEES, deliberately not conflated:
    ///
    ///   SINGLE-FLIGHT is unconditional. At most one run is in flight at a time; later
    ///   callers join the running one. This is the property that caps resource use, so
    ///   it is not opt-in and cannot be switched off.
    ///
    ///   TTL REUSE is opt-in per call (<c>maxStalenessMs</c>, default 0 = never reuse).
    ///   This is where staleness enters, so callers choose. Session-start enrichment
    ///   opts in — janitor findings change on task-completion timescales, not per second.
    ///   Explicit REST refresh endpoints pass 0 and still get single-flight.
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
    /// <typeparam name="T">The scan result. Reference type — the cache stores it by reference.</typeparam>
    internal sealed class ScanCoalescer<T>
        where T : class
    {
        private readonly object _sync = new object();

        /// <summary>The run everyone currently joins, or null when idle.</summary>
        private Task<T>? _inFlight;

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
        /// Reuse the last successful result if it completed within this many ms.
        /// 0 (the default) disables reuse — the caller still gets single-flight.
        /// </param>
        public Task<T> RunAsync(Func<Task<T>> factory, int maxStalenessMs = 0)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_sync)
            {
                if (maxStalenessMs > 0
                    && _last != null
                    && Environment.TickCount64 - _lastCompletedMs <= maxStalenessMs)
                {
                    return Task.FromResult(_last);
                }

                if (_inFlight != null)
                {
                    return _inFlight;
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

                // Runs synchronously only as far as the Task.Run dispatch inside —
                // no scan work happens under the lock.
                _ = CompleteAsync(tcs, factory);

                return tcs.Task;
            }
        }

        private async Task CompleteAsync(TaskCompletionSource<T> tcs, Func<Task<T>> factory)
        {
            try
            {
                var result = await Task.Run(factory).ConfigureAwait(false);

                // Record and release BEFORE completing the TCS: a waiter woken by
                // SetResult must never observe a state where the run is still
                // advertised as in flight.
                lock (_sync)
                {
                    _last = result;
                    _lastCompletedMs = Environment.TickCount64;
                    _inFlight = null;
                }

                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                // Deliberately does NOT touch _last — a failure is not a result, and
                // the next caller must be free to retry rather than inherit it.
                lock (_sync)
                {
                    _inFlight = null;
                }

                tcs.SetException(ex);
            }
        }
    }
}
