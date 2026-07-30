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
            var first = coalescer.RunAsync(Factory);
            while (Volatile.Read(ref runs) == 0) await Task.Yield();

            var joiners = Enumerable.Range(0, 8).Select(_ => coalescer.RunAsync(Factory)).ToArray();

            release.SetResult(true);
            var results = await Task.WhenAll(new[] { first }.Concat(joiners));

            Assert.Equal(1, Volatile.Read(ref runs));
            Assert.All(results, r => Assert.Same(results[0], r));
        }

        [Fact]
        public async Task SingleFlightApplies_EvenWithoutTtl()
        {
            // maxStalenessMs defaults to 0 — the explicit REST endpoints' setting.
            // They opt out of REUSE, but must NOT opt out of single-flight, which is
            // the property that actually bounds concurrent resource use.
            var coalescer = new ScanCoalescer<Result>();
            int runs = 0;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<Result> Factory()
            {
                Interlocked.Increment(ref runs);
                await release.Task.ConfigureAwait(false);
                return new Result();
            }

            var a = coalescer.RunAsync(Factory, maxStalenessMs: 0);
            while (Volatile.Read(ref runs) == 0) await Task.Yield();
            var b = coalescer.RunAsync(Factory, maxStalenessMs: 0);

            release.SetResult(true);
            await Task.WhenAll(a, b);

            Assert.Equal(1, Volatile.Read(ref runs));
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

            var a = coalescer.RunAsync(Factory);
            while (Volatile.Read(ref runs) == 0) await Task.Yield();
            var b = coalescer.RunAsync(Factory);

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
