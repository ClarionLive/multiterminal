using System.Reflection;
using System.Threading.Tasks;
using MultiTerminal.API.Controllers;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 1ce9ddaf — POST /api/session-lineage/register must answer inside the
    /// MCP client's 15s call timeout no matter how expensive the janitor scans
    /// have grown (live repro: ~43s of sequential git spawns at 228 pruned-done
    /// records made every register time out, so sessions never entered the
    /// lifecycle pipeline). The enrichment is raced against a hard budget via
    /// <see cref="SessionLineageController.RaceJanitorFindingsAsync"/>; these
    /// tests pin the race semantics and the budget's headroom.
    /// </summary>
    public class SessionLineageRegisterRaceTests
    {
        [Fact]
        public async Task FindingsArrivingInTime_AreReturnedUnchanged()
        {
            var findings = new { status = "ok" };

            var (result, timedOut, _) = await SessionLineageController.RaceJanitorFindingsAsync(
                () => Task.FromResult<object>(findings), budgetMs: 5000);

            Assert.False(timedOut);
            Assert.Same(findings, result);
        }

        [Fact]
        public async Task NullFindings_CleanBillOfHealth_PassesThrough()
        {
            // BuildJanitorFindingsAsync returns null for "nothing to report" —
            // the race must not turn that into an unavailable marker.
            var (result, timedOut, _) = await SessionLineageController.RaceJanitorFindingsAsync(
                () => Task.FromResult<object>(null), budgetMs: 5000);

            Assert.False(timedOut);
            Assert.Null(result);
        }

        [Fact]
        public async Task OverrunFindings_YieldUnavailableMarker()
        {
            // A findings task that never completes stands in for a janitor scan
            // wedged on git — the caller must get the tri-state "couldn't look"
            // marker (never null, which reads as a clean bill of health).
            var never = new TaskCompletionSource<object>();

            var (result, timedOut, _) = await SessionLineageController.RaceJanitorFindingsAsync(
                () => never.Task, budgetMs: 50);

            Assert.True(timedOut);
            Assert.NotNull(result);
            var status = result.GetType().GetProperty("status")?.GetValue(result);
            Assert.Equal("unavailable", status);
        }

        /// <summary>
        /// Task 36b0b9d5 item ① (1ce9ddaf Run-1 adversary MEDIUM) — THE GUARD THAT
        /// MATTERS. The budget must bound wall-clock even when the enrichment blocks
        /// SYNCHRONOUSLY before its first await, which is the real shape of
        /// BuildJanitorFindingsAsync (project enumeration + the synchronous
        /// ListPrunedWorktreesForDoneTasks read, the latter now able to queue behind
        /// a5ac5f71's write gate).
        ///
        /// THIS TEST WAS DEMONSTRATED TO FAIL against the previous task-taking
        /// signature: budget 50ms, actual ~1000ms, while the helper still reported
        /// TimedOut=true — a guard asserting a bound it never enforced. The other
        /// tests in this class cannot express the defect at all: they pass an
        /// already-created task, which is asynchronous from its first instruction.
        /// Do not "simplify" this back to handing over a task — the factory is what
        /// makes the broken shape unrepresentable.
        /// </summary>
        [Fact]
        public async Task SyncPreAwaitWork_IsInsideTheBudget()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var (_, timedOut, _) = await SessionLineageController.RaceJanitorFindingsAsync(
                () => SyncBlockingFindingsAsync(1000), budgetMs: 50);
            sw.Stop();

            Assert.True(timedOut);
            Assert.True(
                sw.ElapsedMilliseconds < 500,
                $"budget was 50ms but the call took {sw.ElapsedMilliseconds}ms — the "
                + "synchronous pre-await segment ran outside the budget");
        }

        /// <summary>
        /// The overrun path must still hand back the live task, so the caller can
        /// attach its duration log instead of losing the scan silently.
        /// </summary>
        [Fact]
        public async Task OverrunReturnsTheStillRunningTask_SoTheCallerCanObserveIt()
        {
            var (_, timedOut, findingsTask) = await SessionLineageController.RaceJanitorFindingsAsync(
                () => SyncBlockingFindingsAsync(300), budgetMs: 50);

            Assert.True(timedOut);
            Assert.NotNull(findingsTask);

            var completed = await findingsTask;
            var status = completed.GetType().GetProperty("status")?.GetValue(completed);
            Assert.Equal("ok", status);
        }

        /// <summary>
        /// A factory that throws SYNCHRONOUSLY is captured by Task.Run, wins the race
        /// as a faulted task, and PROPAGATES out of the helper — it is not silently
        /// swallowed into a task nobody observes.
        ///
        /// This pins pre-existing semantics deliberately rather than changing them:
        /// before the factory rewrite the throw happened at the call site and became a
        /// 500, and 1ce9ddaf's debugger gate reviewed exactly that ("in-budget fault →
        /// 500 matches pre-change behavior … no regression"). The factory moves WHERE
        /// the throw originates without moving where it surfaces.
        ///
        /// UNREACHABLE IN PRODUCTION TODAY: BuildJanitorFindingsAsync wraps its whole
        /// body in try/catch and returns the unavailable marker, so it cannot throw.
        /// (That is also why the caller's t.IsFaulted branch is dead — see item ⑤.)
        /// This test exists so a future edit that moves work outside that try/catch
        /// discovers the 500 here rather than in production.
        /// </summary>
        [Fact]
        public async Task FactoryThrowingSynchronously_PropagatesRatherThanBeingSwallowed()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await SessionLineageController.RaceJanitorFindingsAsync(
                    () => throw new InvalidOperationException("boom"), budgetMs: 5000));
        }

        /// <summary>
        /// An async method runs synchronously until its first INCOMPLETE await, so
        /// this blocks its caller for <paramref name="blockMs"/> before it ever
        /// returns a task. Stands in for GetAllRegisteredProjects / ResolveByContainment
        /// / the synchronous _db.ListPrunedWorktreesForDoneTasks read.
        /// </summary>
        private static async Task<object> SyncBlockingFindingsAsync(int blockMs)
        {
            System.Threading.Thread.Sleep(blockMs);
            await Task.Yield();
            return new { status = "ok" };
        }

        [Fact]
        public void RegisterBudget_LeavesHeadroomUnderClientTimeout()
        {
            // The MCP client abandons calls at 15s; the janitor budget must sit
            // well under it so registration + enrichment + serialization always
            // answer in time. Guard against a future bump past the cliff.
            var field = typeof(SessionLineageController).GetField(
                "JanitorFindingsBudgetMs", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(field);

            int budget = (int)field.GetRawConstantValue();
            Assert.InRange(budget, 1, 10_000);
        }
    }
}
