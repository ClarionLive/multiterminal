using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Task 36b0b9d5 item ② — <see cref="ScanCoalescer{T}"/> is the answer to
    /// 1ce9ddaf's Run-1 finding (raised independently by Codex security AND the
    /// cross-model adversary) that repeated <c>POST /register</c> calls could pile up
    /// unbounded concurrent janitor scans, each fanning out git subprocesses.
    ///
    /// The two guarantees are tested separately ON PURPOSE, because they are not the
    /// same promise: single-flight is unconditional and is what caps resource use;
    /// TTL reuse is opt-in and is where staleness enters.
    /// </summary>
    public class ScanCoalescerTests
    {
        private sealed class Result
        {
            public int Serial { get; set; }

            public bool Complete { get; set; } = true;

            public int SkippedRecords { get; set; }
        }

        [Fact]
        public async Task ConcurrentCallers_ShareOneRun()
        {
            // THE FINDING, directly: N registers arriving together must not become N scans.
            //
            // Uses a TOLERANT staleness because that is what session-start registration
            // actually passes (SessionStartScanStalenessMs). Pipeline Run 1 corrected this:
            // the test originally used the default 0, which since the adversary's freshness
            // fix means "do not join" — so it was modelling the amplification scenario with
            // the one caller shape that is not allowed to share.
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> Factory()
            {
                Interlocked.Increment(ref runs);
                await release.Task.ConfigureAwait(false);
                return new Result { Serial = 1 };
            }

            // Start the first call and wait until the factory is definitely running,
            // so the joiners cannot race ahead of it (no sleeps — deterministic).
            var first = coalescer.RunAsync(Factory, maxStalenessMs: 60_000);
            while (Volatile.Read(ref runs) == 0) await Task.Yield();

            var joiners = Enumerable.Range(0, 8)
                .Select(_ => coalescer.RunAsync(Factory, maxStalenessMs: 60_000))
                .ToArray();

            release.SetResult(true);
            var results = await Task.WhenAll(new[] { first }.Concat(joiners));

            Assert.Equal(1, Volatile.Read(ref runs));
            Assert.All(results, r => Assert.Same(results[0], r));
        }

        [Fact]
        public async Task SingleFlightApplies_EvenWithoutTtl()
        {
            // maxStalenessMs defaults to 0 — the explicit REST endpoints' setting. They opt
            // out of REUSE (both completed-result and in-flight-join), but must NOT opt out
            // of single-flight, which is what actually bounds concurrent resource use.
            //
            // CORRECTED IN PIPELINE RUN 1. This test previously asserted `runs == 1` — i.e.
            // that a zero-staleness caller JOINS the run in flight. That contradicted
            // ZeroStaleness_AlwaysRunsAgain's stated intent, and both passed because neither
            // mutated state between scan start and join. The adversary gate found the real
            // consequence (an explicit refresh answering from a pre-change snapshot). The
            // contract a strict caller actually gets is: your own run, but never a
            // CONCURRENT one — so what must be asserted here is peak concurrency, not
            // total run count.
            var coalescer = new ScanCoalescer<Result>();
            int concurrent = 0;
            int peak = 0;
            int runs = 0;
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> Factory()
            {
                int live = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref peak, live);
                try
                {
                    if (Interlocked.Increment(ref runs) == 1)
                    {
                        firstStarted.TrySetResult(true);
                        await release.Task.ConfigureAwait(false);
                    }

                    await Task.Yield();
                    return new Result();
                }
                finally
                {
                    Interlocked.Decrement(ref concurrent);
                }
            }

            var a = coalescer.RunAsync(Factory, maxStalenessMs: 0);
            await firstStarted.Task;
            var b = coalescer.RunAsync(Factory, maxStalenessMs: 0);

            release.SetResult(true);
            await Task.WhenAll(a, b);

            Assert.Equal(1, Volatile.Read(ref peak));   // never two scans at once
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen = Volatile.Read(ref target);
            while (value > seen)
            {
                int prior = Interlocked.CompareExchange(ref target, value, seen);
                if (prior == seen) return;
                seen = prior;
            }
        }

        /// <summary>
        /// PIPELINE RUN 1, Codex adversary MEDIUM — the regression this class shipped with.
        ///
        /// A zero-staleness caller (the explicit REST refresh endpoints) must NOT be handed
        /// a run that started before it arrived. The original code returned the in-flight
        /// task unconditionally, so `GET /api/worktrees/pending-merges` could join a scan
        /// that snapshotted state BEFORE the very task completion it was invoked to reveal
        /// and answer `status: ok, count: 0` — a confident wrong answer.
        ///
        /// The fixture mutates the observed state between scan start and join, which is the
        /// ONLY arrangement that exposes it. The two original tests both passed while
        /// encoding contradictory intents: ZeroStaleness_AlwaysRunsAgain checked only the
        /// completed-result cache, and SingleFlightApplies_EvenWithoutTtl asserted a
        /// zero-staleness caller DOES join an in-flight run. Neither mutated state.
        /// </summary>
        [Fact]
        public async Task ZeroStaleness_DoesNotJoinARunThatStartedBeforeItArrived()
        {
            var coalescer = new ScanCoalescer<Result>();
            int observed = 0;              // stands in for the DB state the scan snapshots
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> Factory()
            {
                int snapshot = Volatile.Read(ref observed);   // snapshot at START
                firstStarted.TrySetResult(true);
                await releaseFirst.Task.ConfigureAwait(false);
                return new Result { Serial = snapshot };
            }

            var first = coalescer.RunAsync(Factory, maxStalenessMs: 0);
            await firstStarted.Task;

            // The state changes AFTER the in-flight scan took its snapshot.
            Volatile.Write(ref observed, 42);

            var refresher = coalescer.RunAsync(Factory, maxStalenessMs: 0);

            releaseFirst.SetResult(true);

            Assert.Equal(0, (await first).Serial);        // the first caller's own view
            Assert.Equal(42, (await refresher).Serial);   // the refresher must see the change
        }

        /// <summary>
        /// The freshness fix must not cost the resource bound. However many strict callers
        /// pile up during a slow run, they share ONE follow-up — never one run each.
        /// </summary>
        [Fact]
        public async Task StrictCallersQueuedDuringARun_ShareOneFollowUp()
        {
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> Factory()
            {
                int n = Interlocked.Increment(ref runs);
                if (n == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.ConfigureAwait(false);
                }

                await Task.Yield();
                return new Result { Serial = n };
            }

            var first = coalescer.RunAsync(Factory, maxStalenessMs: 0);
            await firstStarted.Task;

            var queued = Enumerable.Range(0, 6)
                .Select(_ => coalescer.RunAsync(Factory, maxStalenessMs: 0))
                .ToArray();

            releaseFirst.SetResult(true);
            await first;
            var queuedResults = await Task.WhenAll(queued);

            // One original + exactly one shared follow-up. Not seven.
            Assert.Equal(2, Volatile.Read(ref runs));
            Assert.All(queuedResults, r => Assert.Same(queuedResults[0], r));
        }

        /// <summary>
        /// A tolerant caller may still join an in-flight run — that is the amplification
        /// defence and it must survive the freshness fix.
        /// </summary>
        [Fact]
        public async Task TolerantCaller_StillJoinsTheRunInFlight()
        {
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> Factory()
            {
                Interlocked.Increment(ref runs);
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                return new Result();
            }

            var a = coalescer.RunAsync(Factory, maxStalenessMs: 60_000);
            await started.Task;
            var b = coalescer.RunAsync(Factory, maxStalenessMs: 60_000);

            release.SetResult(true);
            var results = await Task.WhenAll(a, b);

            Assert.Equal(1, Volatile.Read(ref runs));
            Assert.Same(results[0], results[1]);
        }

        /// <summary>
        /// A failed run must not poison its queued follow-up — the follow-up runs on its
        /// own merits, so one transient git failure cannot cascade into the next caller.
        /// </summary>
        [Fact]
        public async Task FailedRun_DoesNotFailItsQueuedFollowUp()
        {
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> Factory()
            {
                int n = Interlocked.Increment(ref runs);
                if (n == 1)
                {
                    started.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                    throw new InvalidOperationException("first run blew up");
                }

                await Task.Yield();
                return new Result { Serial = n };
            }

            var failing = coalescer.RunAsync(Factory, maxStalenessMs: 0);
            await started.Task;
            var queued = coalescer.RunAsync(Factory, maxStalenessMs: 0);

            release.SetResult(true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
            Assert.Equal(2, (await queued).Serial);
        }

        [Fact]
        public async Task WithinTtl_ReusesTheLastResult()
        {
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            Task<Result> Factory() => Task.FromResult(new Result { Serial = Interlocked.Increment(ref runs) });

            var first = await coalescer.RunAsync(Factory, maxStalenessMs: 60_000);
            var second = await coalescer.RunAsync(Factory, maxStalenessMs: 60_000);

            Assert.Equal(1, Volatile.Read(ref runs));
            Assert.Same(first, second);
        }

        [Fact]
        public async Task ZeroStaleness_AlwaysRunsAgain()
        {
            // The explicit-refresh contract: a caller that passes 0 must never be
            // served a remembered result, however recent.
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            Task<Result> Factory() => Task.FromResult(new Result { Serial = Interlocked.Increment(ref runs) });

            await coalescer.RunAsync(Factory, maxStalenessMs: 0);
            await coalescer.RunAsync(Factory, maxStalenessMs: 0);

            Assert.Equal(2, Volatile.Read(ref runs));
        }

        [Fact]
        public async Task ExpiredTtl_RunsAgain()
        {
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            Task<Result> Factory() => Task.FromResult(new Result { Serial = Interlocked.Increment(ref runs) });

            await coalescer.RunAsync(Factory, maxStalenessMs: 1);
            await Task.Delay(30);
            await coalescer.RunAsync(Factory, maxStalenessMs: 1);

            Assert.Equal(2, Volatile.Read(ref runs));
        }

        [Fact]
        public async Task InlineCompletingFactory_DoesNotWedgeTheCoalescer()
        {
            // Pins the OBSERVABLE property: a factory that completes immediately must
            // leave the coalescer reusable, not pinned on a finished run. A wedged
            // coalescer fails here as runs == 1.
            //
            // SCOPE, stated so this test is not over-read: it does NOT prove the
            // TaskCompletionSource handoff in RunAsync is load-bearing. That was
            // measured — the worker's-own-task variant also passes this test, because
            // a freshly scheduled Task.Run is never complete by the time the handle is
            // assigned. The wedge window is real but unreachable on demand. This test
            // guards the property; the TCS closes the window by construction. See the
            // matching note in ScanCoalescer.RunAsync.
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            Task<Result> Factory() => Task.FromResult(new Result { Serial = Interlocked.Increment(ref runs) });

            for (int i = 0; i < 5; i++)
            {
                await coalescer.RunAsync(Factory);
            }

            Assert.Equal(5, Volatile.Read(ref runs));
        }

        [Fact]
        public async Task CachedResult_CarriesItsPartialityUnchanged()
        {
            // FALSIFIABILITY (the load-bearing one). A reused PARTIAL scan must still
            // read as partial. If the coalescer ever normalised, merged, or synthesised
            // a result, a cached "couldn't look" would be served as a clean bill of
            // health — exactly the conflation 1ce9ddaf's skip-attribution work exists
            // to prevent.
            var coalescer = new ScanCoalescer<Result>();
            var partial = new Result { Complete = false, SkippedRecords = 7 };

            var first = await coalescer.RunAsync(() => Task.FromResult(partial), maxStalenessMs: 60_000);
            var reused = await coalescer.RunAsync(() => Task.FromResult(new Result()), maxStalenessMs: 60_000);

            Assert.Same(partial, reused);
            Assert.False(reused.Complete);
            Assert.Equal(7, reused.SkippedRecords);
            Assert.Same(first, reused);
        }

        [Fact]
        public async Task Failure_PropagatesToEveryoneSharingTheRun()
        {
            var coalescer = new ScanCoalescer<Result>();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int runs = 0;

            async Task<Result> Factory()
            {
                Interlocked.Increment(ref runs);
                await release.Task.ConfigureAwait(false);
                throw new InvalidOperationException("scan blew up");
            }

            // Tolerant staleness so b genuinely JOINS a — sharing a run is the precondition
            // for "one failure reaches everyone sharing it". A strict caller would queue its
            // own follow-up instead and is covered by FailedRun_DoesNotFailItsQueuedFollowUp.
            var a = coalescer.RunAsync(Factory, maxStalenessMs: 60_000);
            while (Volatile.Read(ref runs) == 0) await Task.Yield();
            var b = coalescer.RunAsync(Factory, maxStalenessMs: 60_000);

            release.SetResult(true);

            await Assert.ThrowsAsync<InvalidOperationException>(() => a);
            await Assert.ThrowsAsync<InvalidOperationException>(() => b);
            Assert.Equal(1, Volatile.Read(ref runs));
        }

        [Fact]
        public async Task Failure_IsNotCached_NextCallerRetries()
        {
            // A failure is not a result. Remembering one would let a single transient
            // git hiccup suppress scanning for the whole TTL.
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;

            Task<Result> Failing()
            {
                Interlocked.Increment(ref runs);
                return Task.FromException<Result>(new InvalidOperationException("nope"));
            }

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coalescer.RunAsync(Failing, maxStalenessMs: 60_000));

            var recovered = await coalescer.RunAsync(
                () => Task.FromResult(new Result { Serial = 99 }), maxStalenessMs: 60_000);

            Assert.Equal(99, recovered.Serial);
            Assert.Equal(1, Volatile.Read(ref runs));
        }

        [Fact]
        public async Task SynchronousPrologue_DoesNotRunUnderTheLock()
        {
            // Same trap as item ①: the janitor scans do real synchronous work (a DB
            // read that now takes LockConn behind a5ac5f71's write gate) before their
            // first await. Task.Run keeps that off the coalescer's lock, so a second
            // caller can still be ADMITTED while the first one's prologue is grinding.
            var coalescer = new ScanCoalescer<Result>();
            var prologueEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePrologue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> BlockingPrologue()
            {
                prologueEntered.TrySetResult(true);
                releasePrologue.Task.GetAwaiter().GetResult(); // synchronous block
                await Task.Yield();
                return new Result();
            }

            var first = coalescer.RunAsync(BlockingPrologue);
            await prologueEntered.Task;

            // If the prologue held the lock, this call could not return at all.
            var second = coalescer.RunAsync(BlockingPrologue);
            Assert.NotNull(second);

            releasePrologue.SetResult(true);
            await Task.WhenAll(first, second);
        }
    }
}
