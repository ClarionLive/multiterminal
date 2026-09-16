using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.API.Controllers;
using MultiTerminal.MCPServer.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task <c>8b270b37</c>. A spawned helper COLLECTS its job from <see cref="SpawnJobStore"/> instead of
    /// MT pushing it. Both push paths lost jobs while reporting success, so the properties pinned here are
    /// the ones that make the pull path honest: a job is handed over once, to the pane it was stored for,
    /// and "nobody collected it" is a state MT can see.
    ///
    /// <para>These are behavioural facts against the real store and the real controller, not source
    /// censuses. The wiring inside <c>MainForm</c>, which a test cannot instantiate, is pinned separately
    /// in <see cref="SpawnJobWiringTests"/>.</para>
    /// </summary>
    public sealed class SpawnJobStoreTests
    {
        private const string DocId = "ab12cd34";
        private const string Job = "Read the nonce file.\nThen write the result file.";

        [Fact]
        public void A_job_is_handed_over_once_and_a_repeat_says_when()
        {
            var clock = new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc);
            var store = new SpawnJobStore(() => clock);
            Assert.True(store.TryAdd(DocId, "Lv1x2", "Alice", Job));

            clock = clock.AddSeconds(21);
            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(DocId, out var first));
            Assert.Equal(Job, first.Job);
            Assert.Equal("Lv1x2", first.AgentName);
            Assert.Equal("Alice", first.SpawnerName);
            Assert.Equal(clock, first.CollectedUtc);

            clock = clock.AddSeconds(30);
            Assert.Equal(SpawnJobCollectOutcome.AlreadyCollected, store.TryCollect(DocId, out var second));

            // The repeat reports the FIRST collection's time, not its own, so a helper told "already
            // collected" learns when that happened.
            Assert.Equal(new DateTime(2026, 9, 16, 18, 0, 21, DateTimeKind.Utc), second.CollectedUtc);
        }

        /// <summary>
        /// Many simultaneous collects: exactly one wins. Asserted on the count of <c>Collected</c> results
        /// AND the event count, because the event is what MT logs as the receipt, and two receipts for one
        /// job would claim two deliveries.
        /// </summary>
        [Fact]
        public async Task Concurrent_collects_produce_exactly_one_winner_and_one_receipt()
        {
            const int racers = 32;
            for (int round = 0; round < 20; round++)
            {
                var store = new SpawnJobStore();
                store.TryAdd(DocId, "Lv1x2", "Alice", Job);
                int receipts = 0;
                store.Collected += (_, _) => Interlocked.Increment(ref receipts);

                using var gate = new Barrier(racers);
                var outcomes = await Task.WhenAll(Enumerable.Range(0, racers).Select(racer => Task.Run(() =>
                {
                    gate.SignalAndWait();
                    return store.TryCollect(DocId, out _);
                })));

                Assert.Equal(1, outcomes.Count(o => o == SpawnJobCollectOutcome.Collected));
                Assert.Equal(racers - 1, outcomes.Count(o => o == SpawnJobCollectOutcome.AlreadyCollected));
                Assert.Equal(1, receipts);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("00000000")]
        public void A_docId_with_no_job_is_not_found(string docId)
        {
            var store = new SpawnJobStore();
            store.TryAdd(DocId, "Lv1x2", "Alice", Job);

            Assert.Equal(SpawnJobCollectOutcome.NotFound, store.TryCollect(docId, out var entry));
            Assert.Null(entry);
        }

        [Fact]
        public void A_second_job_for_the_same_pane_is_refused_and_does_not_replace_the_first()
        {
            var store = new SpawnJobStore();
            Assert.True(store.TryAdd(DocId, "Lv1x2", "Alice", Job));
            Assert.False(store.TryAdd(DocId, "Lv1x2", "Bob", "a different job"));

            store.TryCollect(DocId, out var entry);
            Assert.Equal(Job, entry.Job);
            Assert.Equal("Alice", entry.SpawnerName);
        }

        [Theory]
        [InlineData(null, Job)]
        [InlineData("", Job)]
        [InlineData(DocId, null)]
        [InlineData(DocId, "")]
        [InlineData(DocId, "   ")]
        public void Blank_docIds_and_blank_jobs_are_not_stored(string docId, string job)
        {
            var store = new SpawnJobStore();
            Assert.False(store.TryAdd(docId, "Lv1x2", "Alice", job));
            Assert.Null(store.Get(DocId));
        }

        /// <summary>
        /// The give-up report is claimed at most once, and never for a job that was already collected.
        /// A job collected AFTER the claim still reaches the helper, and the entry says the report went
        /// out first. That flag is what makes MT log the collection as LATE rather than as a normal receipt
        /// sitting beside a contradictory spawn_failed.
        /// </summary>
        [Fact]
        public void The_give_up_report_is_claimed_once_and_a_later_collection_is_marked_late()
        {
            var store = new SpawnJobStore();
            store.TryAdd(DocId, "Lv1x2", "Alice", Job);

            Assert.True(store.TryClaimGiveUpReport(DocId));
            Assert.False(store.TryClaimGiveUpReport(DocId));

            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(DocId, out var entry));
            Assert.True(entry.GiveUpReported);
        }

        [Fact]
        public void No_give_up_report_is_claimed_for_a_collected_job_or_a_pane_with_no_job()
        {
            var store = new SpawnJobStore();
            store.TryAdd(DocId, "Lv1x2", "Alice", Job);
            store.TryCollect(DocId, out var entry);

            Assert.False(store.TryClaimGiveUpReport(DocId));
            Assert.False(entry.GiveUpReported);
            Assert.False(store.TryClaimGiveUpReport("00000000"));
            Assert.False(store.TryClaimGiveUpReport(null));
        }

        /// <summary>
        /// ⚠️ A job is collected only by EXACTLY its pane's docId. Asked behaviourally, through the store,
        /// so it pins the comparer the table actually uses rather than a field that could disagree with it.
        ///
        /// <para>Each variant below is a wrong answer some plausible comparer would give. Case variants
        /// catch <c>OrdinalIgnoreCase</c>; the padded variants catch a trimming key; composed vs decomposed
        /// "Å" catches <c>InvariantCulture</c>, which is case- and space-sensitive, so it passes every other
        /// row, yet treats those two strings as equal. Real docIds are lowercase hex and cannot contain "Å".
        /// That row is here because a comparer checked only against hex inputs says nothing about the next
        /// id format (task c28e6177 is where a name standing in for an id caused exactly that).</para>
        /// </summary>
        [Theory]
        [InlineData("ab12cd34", "AB12CD34")]
        [InlineData("ab12cd34", "Ab12cd34")]
        [InlineData("ab12cd34", " ab12cd34")]
        [InlineData("ab12cd34", "ab12cd34 ")]
        [InlineData("pane-\u00C5", "pane-A\u030A")]
        public void Only_the_exact_docId_collects_the_job(string stored, string asked)
        {
            var store = new SpawnJobStore();
            store.TryAdd(stored, "Lv1x2", "Alice", Job);

            Assert.Equal(SpawnJobCollectOutcome.NotFound, store.TryCollect(asked, out _));
            Assert.Null(store.Get(asked));
            Assert.False(store.TryClaimGiveUpReport(asked));
            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(stored, out _));
        }

        /// <summary>
        /// The status route exists for the plugin's SessionStart hook, whose output is cut to a short
        /// preview. It must report the state WITHOUT handing over or consuming the job.
        /// </summary>
        [Fact]
        public void The_status_route_reports_without_returning_or_consuming_the_job()
        {
            var service = new SpawnService();
            var controller = new SpawnController(service, projectDatabase: null);

            Assert.Equal("no_job", StatusOf(controller.GetJobStatus(DocId)));

            service.Jobs.TryAdd(DocId, "Lv1x2", "Alice", Job);
            var pending = controller.GetJobStatus(DocId);
            Assert.Equal("pending", StatusOf(pending));
            Assert.DoesNotContain("Read the nonce file", Json(pending), StringComparison.Ordinal);
            Assert.DoesNotContain("\"job\"", Json(pending), StringComparison.Ordinal);

            // Asking twice changed nothing: the job is still there to collect.
            Assert.Equal("pending", StatusOf(controller.GetJobStatus(DocId)));
            Assert.Equal("collected", StatusOf(controller.CollectJob(DocId)));
            Assert.Equal("collected", StatusOf(controller.GetJobStatus(DocId)));
        }

        [Fact]
        public void The_collect_route_returns_the_job_once_then_reports_it_collected()
        {
            var service = new SpawnService();
            var controller = new SpawnController(service, projectDatabase: null);
            service.Jobs.TryAdd(DocId, "Lv1x2", "Alice", Job);

            var first = controller.CollectJob(DocId);
            Assert.Equal("collected", StatusOf(first));
            Assert.Contains("Read the nonce file.\\nThen write", Json(first), StringComparison.Ordinal);

            var second = controller.CollectJob(DocId);
            Assert.Equal("already_collected", StatusOf(second));
            Assert.DoesNotContain("Read the nonce file", Json(second), StringComparison.Ordinal);

            Assert.Equal("no_job", StatusOf(controller.CollectJob("00000000")));
        }

        private static string Json(IActionResult result)
        {
            var ok = Assert.IsType<OkObjectResult>(result);
            return JsonSerializer.Serialize(ok.Value);
        }

        private static string StatusOf(IActionResult result)
        {
            using var doc = JsonDocument.Parse(Json(result));
            return doc.RootElement.GetProperty("status").GetString();
        }
    }
}
