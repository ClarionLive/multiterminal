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

            // a5ac5f71 pipeline Run 2 — THE GATE ACQUIRE IS NOW A TERM.
            // Before this, the stack was `deadline + janitor + margin` only. That model had no term for
            // SqliteWriteGate's acquire, so it stayed GREEN after the register path started waiting on the
            // gate — while the endpoint's real worst case moved past the client timeout. A budget guard that
            // omits a term it should bound is worse than no guard: it reads as proof. Two independent
            // pipeline gates found this; the guard did not.
            // RegisterSession takes exactly ONE admission (SessionLineageService wraps the whole unit and the
            // inner per-method gates pass through reentrantly), so the acquire is counted ONCE. If that wrap
            // is ever removed the path takes TWO admissions and this term must double. That assumption is
            // pinned by scripts/verify-writegate.mjs check (5), which requires
            // SessionLineageService.RegisterSession to keep its gate wrap and fails on rename.
            int registerAcquireMs = SqliteWriteGate.RegisterPathAcquireBudgetMs;

            Assert.True(
                registerAcquireMs + SqliteBusyRetry.DefaultDeadlineMs + janitorBudgetMs + requiredMarginMs
                    <= clientTimeoutMs,
                $"register-path budget stack must clear the {clientTimeoutMs}ms client timeout with "
                + $">= {requiredMarginMs}ms margin: gate acquire ({registerAcquireMs}ms) + retry deadline "
                + $"({SqliteBusyRetry.DefaultDeadlineMs}ms) + janitor budget ({janitorBudgetMs}ms) "
                + $"= {registerAcquireMs + SqliteBusyRetry.DefaultDeadlineMs + janitorBudgetMs}ms");

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
        }
    }
}
