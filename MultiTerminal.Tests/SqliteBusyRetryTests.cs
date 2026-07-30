#nullable enable

using System;
using System.Data.SQLite;
using System.Reflection;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Tests for the bounded SQLITE_BUSY retry helper (task a5ac5f71 Phase 3). All retry-path
    /// tests inject zero backoffs so the suite stays fast; the default-backoff values are covered
    /// by the budget-headroom guard instead of by waiting them out.
    /// </summary>
    public class SqliteBusyRetryTests
    {
        private static readonly int[] NoBackoff = { 0, 0 };

        private static SQLiteException Busy() => new(SQLiteErrorCode.Busy, "database is locked");

        [Fact]
        public async Task Success_FirstAttempt_SingleCall()
        {
            int calls = 0;
            int result = await SqliteBusyRetry.ExecuteAsync("test", () => { calls++; return 42; });

            Assert.Equal(42, result);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task Busy_ThenSuccess_RetriesAndReturns()
        {
            int calls = 0;
            int result = await SqliteBusyRetry.ExecuteAsync(
                "test",
                () => ++calls < 3 ? throw Busy() : 42,
                backoffsMs: NoBackoff);

            Assert.Equal(42, result);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task Busy_Always_ThrowsAfterMaxAttempts()
        {
            int calls = 0;
            var ex = await Assert.ThrowsAsync<SQLiteException>(() =>
                SqliteBusyRetry.ExecuteAsync<int>("test", () => { calls++; throw Busy(); }, backoffsMs: NoBackoff));

            Assert.Equal(SQLiteErrorCode.Busy, ex.ResultCode);
            Assert.Equal(SqliteBusyRetry.DefaultMaxAttempts, calls);
        }

        [Fact]
        public async Task WrappedBusy_IsRetried()
        {
            int calls = 0;
            int result = await SqliteBusyRetry.ExecuteAsync(
                "test",
                () => ++calls < 2 ? throw new InvalidOperationException("wrapper", Busy()) : 7,
                backoffsMs: NoBackoff);

            Assert.Equal(7, result);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task NonBusyException_PropagatesImmediately_NoRetry()
        {
            int calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqliteBusyRetry.ExecuteAsync<int>(
                    "test", () => { calls++; throw new InvalidOperationException("logic error"); }, backoffsMs: NoBackoff));

            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task NonBusySqliteError_PropagatesImmediately_NoRetry()
        {
            int calls = 0;
            var ex = await Assert.ThrowsAsync<SQLiteException>(() =>
                SqliteBusyRetry.ExecuteAsync<int>(
                    "test",
                    () => { calls++; throw new SQLiteException(SQLiteErrorCode.Constraint, "constraint failed"); },
                    backoffsMs: NoBackoff));

            Assert.Equal(SQLiteErrorCode.Constraint, ex.ResultCode);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task DeadlineExhausted_GivesUpBeforeMaxAttempts()
        {
            // deadlineMs: 0 means the first busy loss already sits past the ceiling — no retry,
            // even though maxAttempts would allow two more.
            int calls = 0;
            await Assert.ThrowsAsync<SQLiteException>(() =>
                SqliteBusyRetry.ExecuteAsync<int>(
                    "test", () => { calls++; throw Busy(); }, deadlineMs: 0, backoffsMs: NoBackoff));

            Assert.Equal(1, calls);
        }

        [Fact]
        public void BudgetHeadroom_RetryDeadlinePlusJanitorBudget_ClearsClientTimeout()
        {
            // The register endpoint's worst case stacks the retry deadline on top of the janitor
            // findings budget; both must clear the MCP client's 15s call timeout with margin.
            // Mirrors the 1ce9ddaf reflection guard so a future budget bump can't silently
            // reintroduce the timeout this endpoint was fixed for.
            var controller = Type.GetType(
                "MultiTerminal.API.Controllers.SessionLineageController, MultiTerminal");
            Assert.NotNull(controller);
            var field = controller!.GetField(
                "JanitorFindingsBudgetMs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);
            int janitorBudgetMs = (int)field!.GetRawConstantValue()!;

            const int clientTimeoutMs = 15000;
            const int requiredMarginMs = 2000;
            const int busyTimeoutMs = 5000; // Services/MultiterminalDb.cs: connection.BusyTimeout

            // RegisterSession takes exactly ONE gate admission: SessionLineageService wraps the whole
            // close-then-upsert unit, and the inner per-method gates pass through reentrantly. That
            // assumption is pinned by scripts/verify-writegate.mjs check (5), which requires
            // SessionLineageService.RegisterSession to keep its wrap and fails on rename.
            int registerAcquireMs = SqliteWriteGate.RegisterPathAcquireBudgetMs;

            // WHAT THIS BOUND COVERS — and what it does NOT. Scoped deliberately: two earlier versions of
            // this guard were false assurances (a5ac5f71 pipeline Runs 2 and 3).
            //
            // (1) DefaultDeadlineMs is NOT a wall-clock cap on the operation. SqliteBusyRetry starts its
            //     stopwatch before invoking the operation but evaluates the deadline only in the CATCH
            //     path, after a busy failure throws. So it bounds "how long we keep RETRYING", never "how
            //     long one slow SUCCESSFUL attempt may take". This assertion covers the BUSY-FAILURE path
            //     only — the path the 503 + Retry-After contract lives on.
            // (2) The gate acquire is NOT additive to the deadline. EnterWrite runs INSIDE operation(),
            //     hence inside the stopwatch window. Run 2's guard added the two terms together and so
            //     double-counted; that arithmetic is what drove a deadline retune that broke the retry.
            //     The acquire is therefore absent from this sum on purpose.
            Assert.True(
                SqliteBusyRetry.DefaultDeadlineMs + janitorBudgetMs + requiredMarginMs <= clientTimeoutMs,
                $"register-path BUSY-FAILURE budget must clear the {clientTimeoutMs}ms client timeout with "
                + $">= {requiredMarginMs}ms margin: retry deadline ({SqliteBusyRetry.DefaultDeadlineMs}ms) "
                + $"+ janitor budget ({janitorBudgetMs}ms) "
                + $"= {SqliteBusyRetry.DefaultDeadlineMs + janitorBudgetMs}ms");

            // THE PROPERTY THE DEADLINE EXISTS FOR: the retry must actually be able to fire for its own
            // documented trigger — a SQLITE_BUSY raised only AFTER a full busy_timeout wait. That attempt
            // lands at elapsed ≈ busyTimeoutMs, and a retry is taken only if `elapsed + backoff < deadline`.
            // Run 2 set the deadline to 5000, which made this false while every other assertion still
            // passed: the retry silently dropped from two attempts to one in exactly the cross-process
            // contention it exists to absorb. Asserting the VALUE (8000) would not have caught that; this
            // asserts the BEHAVIOUR.
            const int firstBackoffMs = 250; // SqliteBusyRetry.DefaultBackoffsMs[0]
            Assert.True(
                busyTimeoutMs + firstBackoffMs < SqliteBusyRetry.DefaultDeadlineMs,
                $"retry deadline ({SqliteBusyRetry.DefaultDeadlineMs}ms) must exceed busy_timeout "
                + $"({busyTimeoutMs}ms) + first backoff ({firstBackoffMs}ms) or the retry can NEVER fire "
                + "for the case it was written for — a busy failure that already waited out busy_timeout");

            // The acquire budget for this path must stay BELOW the retry deadline. This is the specific
            // inversion that broke the endpoint: with acquire (10000) > deadline (8000), a first attempt that
            // burned the acquire was already past the deadline, and since the deadline is only evaluated
            // BETWEEN attempts the retry could never fire — the backstop was silently dead exactly when the
            // gate had given up and the write was racing ungated.
            Assert.True(
                registerAcquireMs < SqliteBusyRetry.DefaultDeadlineMs,
                $"register-path gate acquire ({registerAcquireMs}ms) must be < the retry deadline "
                + $"({SqliteBusyRetry.DefaultDeadlineMs}ms), or a single attempt can exhaust the whole "
                + "deadline in the gate and the retry will never fire");

            // The generic default must never be used on this path: it exceeds the deadline on its own.
            Assert.True(
                SqliteWriteGate.DefaultAcquireTimeoutMs > SqliteBusyRetry.DefaultDeadlineMs,
                "sanity: this guard exists because the GENERIC acquire default exceeds the retry deadline; "
                + "if that stops being true, re-derive whether the register path still needs its own budget");

            // ── THE DISCLOSED RESIDUAL — the SLOW-SUCCESS path is NOT bounded by anything above. ────────
            // RegisterSession performs TWO separate autocommit writes (CloseOpenSessions, then
            // SaveSessionLineage). Each can wait up to the connection's busy_timeout and still SUCCEED, and
            // a successful attempt is never cut off by the retry deadline. So the real slow-success worst
            // case is: gate acquire + 2x busy_timeout + janitor — which does NOT clear the client timeout
            // with margin, and no assertion here can make it. This over-subscription PREDATES the write
            // gate (two 5s-capable writes already exceeded the post-janitor allowance on their own); the
            // gate merely made it visible.
            //
            // This is asserted as an EXPLICIT INEQUALITY IN THE OTHER DIRECTION so the residual is recorded
            // as a fact rather than as prose someone can skim past. If a future change makes the
            // slow-success path fit — a shorter per-command timeout on this path, one bounded transaction
            // for the pair, or deferring janitor enrichment after slow writes — this assertion FAILS and
            // whoever did it should promote the residual into a real bound and delete this block.
            int slowSuccessWorstCaseMs =
                registerAcquireMs + (2 * busyTimeoutMs) + janitorBudgetMs;
            Assert.True(
                slowSuccessWorstCaseMs + requiredMarginMs > clientTimeoutMs,
                $"the slow-SUCCESS worst case ({slowSuccessWorstCaseMs}ms) now fits inside the "
                + $"{clientTimeoutMs}ms client timeout with {requiredMarginMs}ms margin. That is GOOD news, "
                + "but it means this documented residual is stale: replace this inverted assertion with a "
                + "real forward bound and update the register-path budget docs on SqliteWriteGate "
                + "(RegisterPathAcquireBudgetMs) and SqliteBusyRetry (DefaultDeadlineMs).");
        }
    }
}
