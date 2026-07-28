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

            var (result, timedOut) = await SessionLineageController.RaceJanitorFindingsAsync(
                Task.FromResult<object>(findings), budgetMs: 5000);

            Assert.False(timedOut);
            Assert.Same(findings, result);
        }

        [Fact]
        public async Task NullFindings_CleanBillOfHealth_PassesThrough()
        {
            // BuildJanitorFindingsAsync returns null for "nothing to report" —
            // the race must not turn that into an unavailable marker.
            var (result, timedOut) = await SessionLineageController.RaceJanitorFindingsAsync(
                Task.FromResult<object>(null), budgetMs: 5000);

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

            var (result, timedOut) = await SessionLineageController.RaceJanitorFindingsAsync(
                never.Task, budgetMs: 50);

            Assert.True(timedOut);
            Assert.NotNull(result);
            var status = result.GetType().GetProperty("status")?.GetValue(result);
            Assert.Equal("unavailable", status);
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
