using System;
using System.Data.SQLite;
using System.IO;
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
    public sealed class SpawnJobStoreTests : IDisposable
    {
        private const string DocId = "ab12cd34";
        private const string Job = "Read the nonce file.\nThen write the result file.";
        private const string Nonce = "NONCE-LV1X2";

        private readonly string _testDbPath;
        private readonly string _testMsgDbPath;

        /// <summary>
        /// The controller facts construct a real <see cref="MessageBroker"/> to resolve launch nonces, and a
        /// broker opens both of MT's databases. Same isolation idiom as <c>LaunchNonceLookupTests</c>: two
        /// databases, two variables (task 2ddfc32f).
        /// </summary>
        public SpawnJobStoreTests()
        {
            _testDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_spawnjob_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", _testDbPath);
            _testMsgDbPath = Path.Combine(Path.GetTempPath(), $"multiterminal_spawnjob_msg_{Guid.NewGuid():N}.db");
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", _testMsgDbPath);
        }

        public void Dispose()
        {
            SQLiteConnection.ClearAllPools();
            foreach (var p in new[]
            {
                _testDbPath, _testDbPath + "-wal", _testDbPath + "-shm",
                _testMsgDbPath, _testMsgDbPath + "-wal", _testMsgDbPath + "-shm",
            })
            {
                // Best-effort, as in LaunchNonceLookupTests: a leftover temp file must not fail a test.
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_DB", null);
            Environment.SetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB", null);
        }

        [Fact]
        public void A_job_is_collected_once_and_a_repeat_after_the_window_says_when()
        {
            var clock = new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc);
            var store = new SpawnJobStore(() => clock);
            Assert.True(store.TryAdd(DocId, "Lv1x2", "Alice", Job));

            clock = clock.AddSeconds(21);
            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(DocId, out var first, out string job));
            Assert.Equal(Job, job);
            Assert.Equal("Lv1x2", first.AgentName);
            Assert.Equal("Alice", first.SpawnerName);
            Assert.Equal(clock, first.CollectedUtc);

            clock = clock.Add(SpawnJobStore.RefetchWindow).AddSeconds(1);
            Assert.Equal(SpawnJobCollectOutcome.AlreadyCollected, store.TryCollect(DocId, out var second, out string none));
            Assert.Null(none);

            // The repeat reports the FIRST collection's time, not its own, so a helper told "already
            // collected" learns when that happened.
            Assert.Equal(new DateTime(2026, 9, 16, 18, 0, 21, DateTimeKind.Utc), second.CollectedUtc);
        }

        /// <summary>
        /// Pipeline Run 1 (Codex adversary HIGH): a collect whose REPLY is lost must not lose the job. Inside
        /// the window the job comes back as <c>Refetched</c>, and it is NOT a second receipt: the log would
        /// otherwise claim two deliveries. The window's edge is asserted from both sides, so a window of zero
        /// or of forever both go red.
        /// </summary>
        [Fact]
        public void A_repeat_inside_the_window_gets_the_job_again_without_a_second_receipt()
        {
            var clock = new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc);
            var store = new SpawnJobStore(() => clock);
            int receipts = 0;
            store.Collected += (_, _) => receipts++;
            store.TryAdd(DocId, "Lv1x2", "Alice", Job);

            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(DocId, out _, out _));

            clock = clock.AddSeconds(2);
            Assert.Equal(SpawnJobCollectOutcome.Refetched, store.TryCollect(DocId, out _, out string again));
            Assert.Equal(Job, again);

            clock = clock.Add(SpawnJobStore.RefetchWindow).AddSeconds(-2);
            Assert.Equal(SpawnJobCollectOutcome.Refetched, store.TryCollect(DocId, out _, out _));

            clock = clock.AddSeconds(1);
            Assert.Equal(SpawnJobCollectOutcome.AlreadyCollected, store.TryCollect(DocId, out _, out _));

            Assert.Equal(1, receipts);
        }

        /// <summary>
        /// The text of a job nobody can be handed any more is released LAZILY, by the next TryAdd (asserted
        /// here) or an AlreadyCollected TryCollect, so a long session does not keep every job ever spawned
        /// (code review, Run 1). It does not pin release on a deadline: there is none. The length survives
        /// for the receipt log.
        /// </summary>
        [Fact]
        public void The_job_text_is_released_once_the_window_closes()
        {
            var clock = new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc);
            var store = new SpawnJobStore(() => clock);
            store.TryAdd(DocId, "Lv1x2", "Alice", Job);
            store.TryCollect(DocId, out var entry, out _);
            Assert.Equal(Job, entry.Job);

            clock = clock.Add(SpawnJobStore.RefetchWindow).AddSeconds(1);

            // Before any releasing call, the expired text is still held: the release is lazy, by design.
            Assert.Equal(Job, entry.Job);

            // TryAdd is one of the two calls that release expired text; adding another pane's job is the ordinary case.
            store.TryAdd("ef56ab78", "Lv1x3", "Alice", "another job");
            Assert.Null(entry.Job);
            Assert.Equal(Job.Length, entry.JobLength);
        }

        /// <summary>
        /// A throwing receipt subscriber must not turn a collect into an error: by the time it runs, the job
        /// is already marked collected, so a propagated exception would leave the caller without a job the
        /// store considers delivered, with nothing left to retry (code review, Run 1). The failure is
        /// counted, so it is not silent, and later subscribers still run.
        /// </summary>
        [Fact]
        public void A_throwing_receipt_subscriber_does_not_lose_the_job()
        {
            var store = new SpawnJobStore();
            bool laterSubscriberRan = false;
            store.Collected += (_, _) => throw new InvalidOperationException("log sink down");
            store.Collected += (_, _) => laterSubscriberRan = true;
            store.TryAdd(DocId, "Lv1x2", "Alice", Job);

            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(DocId, out _, out string job));
            Assert.Equal(Job, job);
            Assert.True(laterSubscriberRan);
            Assert.Equal(1, store.ReceiptSubscriberFailures);
        }

        /// <summary>
        /// Many simultaneous collects: exactly one wins. Asserted on the count of <c>Collected</c> results
        /// AND the event count, because the event is what MT logs as the receipt, and two receipts for one
        /// job would claim two deliveries. The losers land inside the re-fetch window, so they are
        /// <c>Refetched</c>.
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
                    return store.TryCollect(DocId, out _, out _);
                })));

                Assert.Equal(1, outcomes.Count(o => o == SpawnJobCollectOutcome.Collected));
                Assert.Equal(racers - 1, outcomes.Count(o => o == SpawnJobCollectOutcome.Refetched));
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

            Assert.Equal(SpawnJobCollectOutcome.NotFound, store.TryCollect(docId, out var entry, out _));
            Assert.Null(entry);
        }

        [Fact]
        public void A_second_job_for_the_same_pane_is_refused_and_does_not_replace_the_first()
        {
            var store = new SpawnJobStore();
            Assert.True(store.TryAdd(DocId, "Lv1x2", "Alice", Job));
            Assert.False(store.TryAdd(DocId, "Lv1x2", "Bob", "a different job"));

            store.TryCollect(DocId, out var entry, out _);
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

            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(DocId, out var entry, out _));
            Assert.True(entry.GiveUpReported);
        }

        [Fact]
        public void No_give_up_report_is_claimed_for_a_collected_job_or_a_pane_with_no_job()
        {
            var store = new SpawnJobStore();
            store.TryAdd(DocId, "Lv1x2", "Alice", Job);
            store.TryCollect(DocId, out var entry, out _);

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

            Assert.Equal(SpawnJobCollectOutcome.NotFound, store.TryCollect(asked, out _, out _));
            Assert.Null(store.Get(asked));
            Assert.False(store.TryClaimGiveUpReport(asked));
            Assert.Equal(SpawnJobCollectOutcome.Collected, store.TryCollect(stored, out _, out _));
        }

        /// <summary>
        /// The status route exists for the plugin's SessionStart hook, whose output is cut to a short
        /// preview. It must report the state WITHOUT handing over or consuming the job.
        /// </summary>
        [Fact]
        public void The_status_route_reports_without_returning_or_consuming_the_job()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Lv1x2", docId: DocId, nonce: Nonce);
            var service = new SpawnService();
            var controller = new SpawnController(service, projectDatabase: null, broker);

            Assert.Equal("no_job", StatusOf(controller.GetJobStatus(DocId)));

            service.Jobs.TryAdd(DocId, "Lv1x2", "Alice", Job);
            var pending = controller.GetJobStatus(DocId);
            Assert.Equal("pending", StatusOf(pending));
            Assert.DoesNotContain("Read the nonce file", Json(pending), StringComparison.Ordinal);
            Assert.DoesNotContain("\"job\"", Json(pending), StringComparison.Ordinal);

            // Asking twice changed nothing: the job is still there to collect.
            Assert.Equal("pending", StatusOf(controller.GetJobStatus(DocId)));
            Assert.Equal("collected", StatusOf(controller.CollectJob(DocId, Nonce)));
            Assert.Equal("collected", StatusOf(controller.GetJobStatus(DocId)));
        }

        /// <summary>
        /// The pane's own helper collects, and an immediate repeat (a retry after a lost reply) gets the job
        /// again as <c>refetched</c>. The after-the-window answer is pinned on the store, which has a clock.
        /// </summary>
        [Fact]
        public void The_collect_route_returns_the_job_then_a_prompt_repeat_is_a_refetch()
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Lv1x2", docId: DocId, nonce: Nonce);
            broker.RegisterTerminal("Lv0", docId: "00000000", nonce: "NONCE-LV0");
            var service = new SpawnService();
            var controller = new SpawnController(service, projectDatabase: null, broker);
            service.Jobs.TryAdd(DocId, "Lv1x2", "Alice", Job);

            var first = controller.CollectJob(DocId, Nonce);
            Assert.Equal("collected", StatusOf(first));
            Assert.Contains("Read the nonce file.\\nThen write", Json(first), StringComparison.Ordinal);

            var second = controller.CollectJob(DocId, Nonce);
            Assert.Equal("refetched", StatusOf(second));
            Assert.Contains("Read the nonce file.\\nThen write", Json(second), StringComparison.Ordinal);
            Assert.Contains("\"collectedUtc\"", Json(second), StringComparison.Ordinal);

            Assert.Equal("no_job", StatusOf(controller.CollectJob("00000000", "NONCE-LV0")));
        }

        /// <summary>
        /// ⚠️ Pipeline Run 1 (Codex security HIGH A01, adversary MEDIUM): a docId alone must not collect.
        /// Each refused caller is a wrong answer a plausible check would give. No nonce and a wrong nonce
        /// catch a missing check. <b>Another live pane's valid nonce</b> is the discriminator: a check that
        /// asks only "is this SOME terminal's nonce" accepts it, and only comparing that terminal's docId to
        /// the route refuses it. After every refusal the job must still be pending and collectable by its
        /// owner, because a refusal that consumed the job would still fake the receipt.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("NONCE-WRONG")]
        [InlineData("NONCE-LV1X3")]
        public void A_caller_without_the_panes_own_nonce_is_refused_and_consumes_nothing(string presented)
        {
            using var broker = new MessageBroker();
            broker.RegisterTerminal("Lv1x2", docId: DocId, nonce: Nonce);
            broker.RegisterTerminal("Lv1x3", docId: "ef56ab78", nonce: "NONCE-LV1X3");
            var service = new SpawnService();
            var controller = new SpawnController(service, projectDatabase: null, broker);
            service.Jobs.TryAdd(DocId, "Lv1x2", "Alice", Job);

            var refused = Assert.IsType<ObjectResult>(controller.CollectJob(DocId, presented));
            Assert.Equal(401, refused.StatusCode);
            Assert.DoesNotContain("Read the nonce file", JsonSerializer.Serialize(refused.Value), StringComparison.Ordinal);

            Assert.False(service.Jobs.Get(DocId).IsCollected);
            Assert.Equal("collected", StatusOf(controller.CollectJob(DocId, Nonce)));
        }

        /// <summary>
        /// With no broker to resolve a nonce, collection fails closed rather than falling back to trusting
        /// the docId.
        /// </summary>
        [Fact]
        public void Without_a_broker_every_collect_is_refused()
        {
            var service = new SpawnService();
            var controller = new SpawnController(service, projectDatabase: null, broker: null);
            service.Jobs.TryAdd(DocId, "Lv1x2", "Alice", Job);

            var refused = Assert.IsType<ObjectResult>(controller.CollectJob(DocId, Nonce));
            Assert.Equal(401, refused.StatusCode);
            Assert.False(service.Jobs.Get(DocId).IsCollected);
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
