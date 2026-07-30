#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// <para>Process-wide fairness gate for WRITES to the shared <c>multiterminal.db</c> file
    /// (task a5ac5f71 Phase 2, folding ticket 7737d613's cross-owner dimension).</para>
    ///
    /// <para><b>The defect this fixes is FAIRNESS, not timeout or chunk size.</b> Per bb2b0104 every
    /// connection owner (TaskDatabase, CodeGraphDatabase, SessionMemoryDatabase, KnowledgeDatabase,
    /// ProjectDatabase, …) opens its OWN connection and serializes only ITS OWN threads
    /// (<c>TaskDatabase.LockConn()</c>, CodeGraphDatabase's sync gate). Nothing coordinated writers
    /// ACROSS owners. SQLite's file-level write lock is the contended resource and <c>busy_timeout</c>
    /// retry carries <b>no fairness guarantee</b> — so a writer that reacquires every ~1ms starves a
    /// competitor indefinitely, no matter how large the timeout.</para>
    ///
    /// <para>Phase 1 measured exactly that: <c>CodeGraph.ChunkedWritePass</c> ran 9 write transactions
    /// across an 8.86s window with <b>0–1ms gaps</b> — a ~<b>99.6% write-lock duty cycle</b> — while the
    /// longest single hold (3151ms) stayed comfortably UNDER the 5s <c>busy_timeout</c>. The victim
    /// burned its full 5s and never saw a window wider than 1ms. Ironically the chunking introduced by
    /// 93ad8184 (to break up one 13–23s hold) is what created this shape: it traded a
    /// timeout-exceeding hold for a starvation chain.</para>
    ///
    /// <para><b>Writes only.</b> WAL readers never block and never contend, so gating reads would
    /// serialize the whole app for zero benefit.</para>
    ///
    /// <para><b>Cross-process limitation (do not overclaim).</b> This gate is in-process. It cannot
    /// serialize against <c>mcp-session-history</c>'s better-sqlite3 indexer, a separate OS process on
    /// the same DB family (see <see cref="MultiterminalDb"/>). <c>busy_timeout</c> plus
    /// <see cref="SqliteBusyRetry"/> remain the backstop for that residual race. Phase 2 bounds the
    /// in-process wait; Phase 3 absorbs what is left.</para>
    ///
    /// <para><b>LOCK ORDERING — GLOBAL BEFORE LOCAL. Non-negotiable.</b> A writer that also takes its
    /// owner's local lock (<c>TaskDatabase.LockConn()</c>, <c>CodeGraphDatabase</c>'s
    /// <c>_syncLock</c>, <c>KnowledgeDatabase._gate</c>) MUST call <see cref="EnterWrite"/>
    /// <b>first</b>, then the local lock. The reverse order was the shape shipped in the first Phase 2
    /// pass and it is a genuine defect: the acquire below can wait up to
    /// <see cref="DefaultAcquireTimeoutMs"/>, and serving that wait while holding
    /// <c>TaskDatabase._dbLock</c> stalls all ~162 <c>LockConn()</c> sites — <b>reads included</b> —
    /// rather than just the writers the gate is meant to order. It also inverted ordering relative to
    /// <c>CodeGraph.ChunkedWritePass</c>, which always took the gate first.</para>
    ///
    /// <para><b>Why the reverse direction is nonetheless safe — boundedness, NOT acyclicity.</b> It is
    /// tempting to argue that a gate holder waiting on a local lock cannot cycle "because nothing holding
    /// a local lock ever waits on this gate". That argument is FALSE, and its counterexample is named in
    /// this very design: <c>SessionMemoryDatabase.IndexSessionFile</c> holds its owner lock and then waits
    /// on this gate (it is the one entry in <c>ORDERING_EXEMPT</c>, because hoisting there would hold the
    /// global write gate across per-chunk embedding). So a cycle IS constructible on paper. What makes it
    /// harmless is that <b>this acquire is bounded</b>: after <paramref name="timeoutMs"/> the waiter
    /// gives up and proceeds ungated, so the worst case is a bounded stall plus a Warning, never a
    /// permanent hang. Do not upgrade this to an acyclicity claim, and do not make the acquire unbounded
    /// on the theory that no cycle exists. Note the
    /// prose at <c>TaskDatabase.cs</c> says <c>LockConn()</c> must be the "FIRST statement"; the rule
    /// that is actually machine-enforced (<c>scripts/verify-taskdb-gate.mjs</c>) is
    /// gate-before-first-<c>_connection</c>-use, and the documented purpose is that the local lock must
    /// span the whole command/reader/transaction lifecycle. <see cref="EnterWrite"/> touches no
    /// connection, so hoisting it above <c>LockConn()</c> satisfies both the census and that intent.
    /// <c>scripts/verify-writegate.mjs</c> check (4) enforces this ordering.</para>
    /// </summary>
    public static class SqliteWriteGate
    {
        /// <summary>
        /// How long a writer waits for the gate before giving up and proceeding UNGATED. Chosen above
        /// the longest hold Phase 1 observed (3151ms for a 25-file code-graph chunk) with headroom for
        /// a slower disk, so the fall-through is a genuine anomaly rather than routine.
        /// </summary>
        public const int DefaultAcquireTimeoutMs = 10000;

        /// <summary>
        /// <para>Acquire budget for the <c>register_session</c> write unit specifically — deliberately far
        /// below <see cref="DefaultAcquireTimeoutMs"/> (task a5ac5f71 pipeline Run 2).</para>
        ///
        /// <para>That endpoint is the only gated path with a HARD external deadline: the MCP client gives up
        /// after 15s. Its budget stack is near-saturated before the gate is even considered, because
        /// <c>RegisterSession</c> performs two autocommit writes that can each burn the 5s
        /// <c>busy_timeout</c> — an over-subscription that predates this ticket. The default 10s acquire did
        /// not fit in what remains, and because <see cref="SqliteBusyRetry"/> only evaluates its deadline
        /// BETWEEN attempts, a first attempt that burned the default silently disabled the retry that is
        /// this endpoint's designed backstop.</para>
        ///
        /// <para>2000ms is chosen to stay useful rather than decorative: it still absorbs a typical
        /// code-graph chunk hold, while a longer wait than this is better served by failing soft and letting
        /// <see cref="SqliteBusyRetry"/> return 503 + <c>Retry-After</c> than by making the caller wait. The
        /// honest trade: against the 3151ms worst-case hold Phase 1 measured, this budget can fall through,
        /// and then the write races as it did pre-Phase-2 — which is precisely what the retry is for.
        /// <c>BudgetHeadroom_*</c> in the tests keeps this value inside the client timeout with every other
        /// term named, so it cannot drift silently again.</para>
        /// </summary>
        public const int RegisterPathAcquireBudgetMs = 2000;

        private const string LogSource = "SqliteWriteGate";

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Per-thread reentrancy depth. <b>THREAD-local, deliberately not AsyncLocal.</b> The gated
        /// surface is fully synchronous — <c>TaskDatabase</c>'s own gate is a reentrant
        /// <c>Monitor</c> for the same reason and its docs (TaskDatabase.cs "Never in an iterator or
        /// async method") keep these paths sync on purpose. Thread affinity therefore matches the
        /// ratified dialect exactly. <c>AsyncLocal</c> would additionally flow into child tasks
        /// spawned INSIDE a gate scope, making them falsely believe they already hold the gate and
        /// letting a genuinely concurrent write slip through ungated.
        /// </summary>
        private static readonly ThreadLocal<int> Depth = new ThreadLocal<int>();

        private static DebugLogService? _log;

        /// <summary>
        /// Wires the shared <see cref="DebugLogService"/> sink. Called once by MainForm alongside
        /// <see cref="WriteContentionDiagnostics.SetLogger"/>. Before wiring (or in unit tests) output
        /// falls back to <see cref="Debug.WriteLine(string)"/>.
        /// </summary>
        public static void SetLogger(DebugLogService? logger) => _log = logger;

        /// <summary>
        /// True when the calling thread is already inside a gate scope. Exposed for tests and for
        /// assertions; callers should not branch on it — <see cref="EnterWrite"/> already handles
        /// reentrancy by passing through.
        /// </summary>
        public static bool IsHeldByCurrentThread => Depth.Value > 0;

        /// <summary>
        /// <para>Acquires the write gate and registers the Phase 1 contention scope, returning ONE
        /// disposable that releases both. Wrap the full lifetime of a write transaction (or a
        /// single autocommit write statement) on multiterminal.db:</para>
        /// <code>using var gate = SqliteWriteGate.EnterWrite("CodeGraph.ChunkedWritePass", detail);</code>
        ///
        /// <para><b>One entry point on purpose.</b> Acquisition is co-extensive with the Phase 1
        /// diagnostics census, so the GATED set and the OBSERVED set cannot drift: a future write that
        /// forgets this call forgets its diagnostics too — one omission to catch, not two.</para>
        ///
        /// <para><b>Reentrant.</b> If this thread already holds the gate the call passes through
        /// without re-acquiring (a nested write would otherwise self-deadlock). The nested scope is
        /// still registered with <see cref="WriteContentionDiagnostics"/> so a busy dump shows the
        /// inner owner.</para>
        ///
        /// <para><b>Fail-soft, never a new hang.</b> If the gate cannot be acquired within
        /// <paramref name="timeoutMs"/> the write proceeds UNGATED with a warning rather than
        /// blocking. Load-bearing: some writes run on the UI thread (e.g.
        /// <c>PurgeOrphanEmptyNoteTabs</c> from MainForm startup), where an unbounded wait behind a
        /// code-graph chunk would visibly freeze the app.</para>
        ///
        /// <para><b>Honest bound on "no worse than the status quo".</b> An earlier revision of this doc
        /// claimed a fall-through "can never be worse than the status quo". That is true of the SQLite
        /// write lock itself, but it is NOT true of wall-clock latency once a local lock is involved:
        /// the fall-through path still spent <paramref name="timeoutMs"/> waiting first, so a writer's
        /// total worst case is the acquire budget PLUS the pre-existing <c>busy_timeout</c>. The
        /// global-before-local ordering rule above is what keeps that added latency confined to
        /// writers instead of stalling an owner's readers behind its local lock. Fall-throughs are
        /// logged at Warning precisely because they are the case where the fix stops helping.</para>
        /// </summary>
        /// <param name="owner">Code path holding the write, e.g. "SessionMemory.IndexSessionFile".</param>
        /// <param name="detail">Per-call specifics (file name, chunk range) for the busy dump.</param>
        /// <param name="timeoutMs">Acquire budget; defaults to <see cref="DefaultAcquireTimeoutMs"/>.</param>
        public static IDisposable EnterWrite(string owner, string? detail = null, int timeoutMs = DefaultAcquireTimeoutMs)
        {
            // Reentrant pass-through FIRST: a nested write must never queue behind the gate its own
            // thread is holding. Checked before any wait, so nesting costs nothing.
            if (Depth.Value > 0)
            {
                Depth.Value++;
                return new GateScope(WriteContentionDiagnostics.BeginWrite(owner, detail), acquired: false);
            }

            bool acquired;
            var waited = Stopwatch.StartNew();
            try
            {
                acquired = Gate.Wait(timeoutMs);
            }
            catch (ObjectDisposedException)
            {
                // Gate torn down during shutdown — never block a late write on a disposed primitive.
                acquired = false;
            }

            waited.Stop();

            if (!acquired)
            {
                // Fail-soft: proceed without the gate. Logged at Warning because a fall-through means
                // this write is once again racing unfairly — the exact condition Phase 2 exists to
                // prevent — so a recurring line here is a real signal, not noise.
                Log(
                    DebugLogLevel.Warning,
                    $"Write gate NOT acquired within {timeoutMs}ms by {owner}{FormatDetail(detail)} — " +
                    "proceeding UNGATED (fail-soft). This write races unfairly; busy_timeout and " +
                    "SqliteBusyRetry are its only protection.");
            }
            else if (waited.ElapsedMilliseconds >= WriteContentionDiagnostics.LongHoldWarnMs)
            {
                // A long QUEUE wait is healthy behaviour (the gate working as designed) but worth
                // seeing: it quantifies how long fairness is costing this writer, and distinguishes
                // "waited then succeeded" from the starvation it replaced.
                Log(
                    DebugLogLevel.Info,
                    $"Write gate queued {waited.ElapsedMilliseconds}ms before admitting {owner}{FormatDetail(detail)}.");
            }

            if (acquired)
            {
                Depth.Value = 1;
            }

            return new GateScope(WriteContentionDiagnostics.BeginWrite(owner, detail), acquired);
        }

        private static void Log(DebugLogLevel level, string message)
        {
            var log = _log;
            if (log != null)
            {
                log.Log(LogSource, level, message);
            }
            else
            {
                Debug.WriteLine($"[{LogSource}] {level}: {message}");
            }
        }

        private static string FormatDetail(string? detail)
            => string.IsNullOrEmpty(detail) ? string.Empty : " [" + detail + "]";

        /// <summary>
        /// Releases, in order: the diagnostics scope, then the semaphore (only if this scope actually
        /// acquired it — a reentrant or failed-acquire scope must NOT release someone else's permit).
        /// Idempotent, and never throws out of Dispose.
        /// </summary>
        private sealed class GateScope : IDisposable
        {
            private readonly IDisposable _contention;
            private readonly bool _acquired;
            private int _disposed;

            public GateScope(IDisposable contention, bool acquired)
            {
                _contention = contention;
                _acquired = acquired;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                try
                {
                    _contention.Dispose();
                }
                finally
                {
                    // Depth unwinds uniformly: `using var` guarantees LIFO disposal, so a decrement is
                    // correct for the outermost (acquired, depth 1 -> 0) and nested (depth n -> n-1)
                    // scopes alike. A failed-acquire (ungated) scope never incremented, hence the guard.
                    if (Depth.Value > 0)
                    {
                        Depth.Value--;
                    }

                    if (_acquired)
                    {
                        try { Gate.Release(); }
                        catch (ObjectDisposedException) { /* shutdown; nothing to release */ }
                        catch (SemaphoreFullException) { /* defensive: never surface from Dispose */ }
                    }
                }
            }
        }
    }
}
