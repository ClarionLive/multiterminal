#nullable enable

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// <para>Bounded retry-with-backoff for SQLite writes that can transiently lose the
    /// multiterminal.db write-lock race past the centralized 5s busy_timeout (task a5ac5f71).
    /// During MT startup, bulk indexers (session-memory crash recovery, code-graph sweeps, the
    /// external mcp-session-history process) can contend the file hard enough that a foreground
    /// API write throws SQLITE_BUSY even after its full busy_timeout wait. For an idempotent
    /// write, one short in-process retry converts that transient loss into a slightly slower
    /// success instead of a hard 500.</para>
    ///
    /// <para>Only busy/locked losses are retried (<see cref="WriteContentionDiagnostics.IsBusyException"/>);
    /// every other exception propagates on the first throw — retrying a constraint violation or a
    /// logic error just repeats it. Callers MUST only wrap operations that are safe to re-run
    /// (idempotent UPDATE/upsert); this helper cannot know.</para>
    ///
    /// <para>Every busy loss — including ones a later attempt recovers from — emits the
    /// <see cref="WriteContentionDiagnostics.LogBusy"/> holder snapshot, so a contention event
    /// that self-heals still leaves the Phase 1 evidence trail instead of becoming invisible.</para>
    /// </summary>
    public static class SqliteBusyRetry
    {
        /// <summary>Default cap on total attempts (first try + retries).</summary>
        public const int DefaultMaxAttempts = 3;

        /// <summary>
        /// <para>Default total-elapsed ceiling across all attempts, in milliseconds.</para>
        ///
        /// <para>Sized for the register endpoint's budget stack: a busy attempt may itself have burned the
        /// full 5s <c>busy_timeout</c>, and the caller still needs headroom for the 3s janitor-findings
        /// budget inside the MCP client's 15s call timeout — 8s + 3s leaves 4s of margin.
        /// <c>BudgetHeadroom_*</c> in the tests holds this relationship.</para>
        ///
        /// <para><b>THIS VALUE MUST STAY ABOVE <c>busy_timeout</c> + one backoff (≈5250ms).</b> Task
        /// a5ac5f71 pipeline Run 2 lowered it to 5000 to make an additive budget model fit, and pipeline
        /// Run 3 showed that was wrong twice over. The model double-counted: the gate acquire happens
        /// INSIDE <see cref="ExecuteAsync"/>'s stopwatch (the acquire is part of <c>operation()</c>), so it
        /// is not additive to this deadline. And 5000 broke the helper's own purpose — the documented
        /// trigger is "SQLITE_BUSY *after* a full <c>busy_timeout</c> wait", which lands at elapsed ≈ 5000,
        /// where <c>elapsed + delayMs >= deadlineMs</c> is already true. The retry silently dropped from two
        /// attempts to one in exactly the cross-process contention it exists to absorb, while the guard
        /// asserting <c>acquire &lt; deadline</c> still passed. Reverted to 8000.</para>
        ///
        /// <para>What this deadline does NOT bound: a slow but SUCCESSFUL attempt. It is evaluated only in
        /// the catch path, so it caps retrying, never one in-flight operation. See the disclosed residual in
        /// <c>BudgetHeadroom_*</c>.</para>
        /// </summary>
        public const int DefaultDeadlineMs = 8000;

        private static readonly int[] DefaultBackoffsMs = { 250, 750 };

        /// <summary>
        /// Runs <paramref name="operation"/>, retrying only busy/locked SQLite failures with
        /// bounded backoff. Gives up — rethrowing the last busy exception — when
        /// <paramref name="maxAttempts"/> is exhausted or the next retry would start past
        /// <paramref name="deadlineMs"/> total elapsed. The operation itself runs synchronously
        /// (ADO.NET writes are sync); only the backoff awaits.
        /// </summary>
        /// <param name="victim">Diagnostic identity for the busy dump (e.g. "RestApi POST /api/session-lineage/register").</param>
        /// <param name="operation">The idempotent write to run.</param>
        /// <param name="maxAttempts">Total attempt cap; defaults to <see cref="DefaultMaxAttempts"/>.</param>
        /// <param name="deadlineMs">Total-elapsed ceiling; defaults to <see cref="DefaultDeadlineMs"/>.</param>
        /// <param name="backoffsMs">Delays between attempts (test seam); index i waits after attempt i+1. Defaults to 250ms, 750ms.</param>
        public static async Task<T> ExecuteAsync<T>(
            string victim,
            Func<T> operation,
            int maxAttempts = DefaultMaxAttempts,
            int deadlineMs = DefaultDeadlineMs,
            int[]? backoffsMs = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (maxAttempts < 1) maxAttempts = 1;
            backoffsMs ??= DefaultBackoffsMs;

            var elapsed = Stopwatch.StartNew();
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (Exception ex) when (WriteContentionDiagnostics.IsBusyException(ex))
                {
                    int delayMs = backoffsMs[Math.Min(attempt - 1, backoffsMs.Length - 1)];
                    bool outOfAttempts = attempt >= maxAttempts;
                    // Inclusive ceiling: a retry that would START at or past the deadline is not taken.
                    bool outOfTime = elapsed.ElapsedMilliseconds + delayMs >= deadlineMs;
                    bool willRetry = !outOfAttempts && !outOfTime;

                    WriteContentionDiagnostics.LogBusy(
                        $"{victim} (attempt {attempt}/{maxAttempts}, {(willRetry ? $"retrying in {delayMs}ms" : outOfAttempts ? "attempts exhausted" : "deadline reached")})",
                        ex.GetBaseException().Message);

                    if (!willRetry) throw;
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }
        }
    }
}
