#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Tests for the process-wide write fairness gate (task a5ac5f71 Phase 2, folding 7737d613).
    ///
    /// <para>The gate is a STATIC process-wide primitive, so these tests never assume it is
    /// uncontended — every case either acquires it deliberately or asserts a property that holds
    /// regardless of who else is writing. Tests that must succeed use the generous default timeout;
    /// only the deliberate fall-through case uses a short one.</para>
    ///
    /// <para>Where a regression would HANG rather than fail (reentrancy), the work runs on a task
    /// with a bounded wait so the failure surfaces as a fast assertion instead of a CI timeout.</para>
    /// </summary>
    public class SqliteWriteGateTests
    {
        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(15);

        /// <summary>
        /// "Did this task finish within the budget?" as a bool rather than an exception, using the
        /// ratified Option A timeout dialect (task b840ddee): WhenAny against a bare Task.Delay, no
        /// CancellationTokenSource. xUnit1031 forbids blocking Task.Wait in tests, so the waits here
        /// are awaited — which also means a reentrancy regression surfaces as a fast assertion failure
        /// instead of hanging the run.
        /// </summary>
        private static async Task<bool> CompletesAsync(Task task)
            => await Task.WhenAny(task, Task.Delay(Generous)).ConfigureAwait(false) == task;

        [Fact]
        public void Enter_AcquiresAndReleases()
        {
            Assert.False(SqliteWriteGate.IsHeldByCurrentThread);
            using (SqliteWriteGate.EnterWrite("Test.Simple"))
            {
                Assert.True(SqliteWriteGate.IsHeldByCurrentThread);
            }

            Assert.False(SqliteWriteGate.IsHeldByCurrentThread);
        }

        /// <summary>
        /// THE critical case. TaskDatabase's own gate is a reentrant Monitor precisely because gated
        /// methods nest (TaskDatabase.cs warns against swapping in a non-reentrant SemaphoreSlim
        /// without re-auditing the call graph). A non-reentrant global gate would self-deadlock on the
        /// first nested write — so this asserts the nested acquire COMPLETES, and that the outer scope
        /// still holds the gate after the inner one unwinds.
        /// </summary>
        [Fact]
        public async Task Reentrant_NestedEnter_DoesNotDeadlock_AndUnwindsToOuter()
        {
            bool innerHeld = false, outerStillHeldAfterInner = false, releasedAtEnd = true;

            var work = Task.Run(() =>
            {
                using (SqliteWriteGate.EnterWrite("Test.Outer"))
                {
                    using (SqliteWriteGate.EnterWrite("Test.Inner"))
                    {
                        innerHeld = SqliteWriteGate.IsHeldByCurrentThread;
                    }

                    // Disposing the INNER scope must not release the gate the outer scope owns.
                    outerStillHeldAfterInner = SqliteWriteGate.IsHeldByCurrentThread;
                }

                releasedAtEnd = SqliteWriteGate.IsHeldByCurrentThread;
            });

            Assert.True(await CompletesAsync(work), "nested EnterWrite deadlocked — the gate is not reentrant");
            Assert.True(innerHeld);
            Assert.True(outerStillHeldAfterInner, "inner scope's dispose wrongly released the outer scope's permit");
            Assert.False(releasedAtEnd, "outer scope did not release the gate");
        }

        /// <summary>
        /// Fail-soft: a writer that cannot get the gate proceeds UNGATED rather than blocking. This is
        /// what keeps the change strictly no-worse-than-before, and it is load-bearing for writes that
        /// run on the UI thread (PurgeOrphanEmptyNoteTabs from MainForm startup), where an unbounded
        /// wait behind a code-graph chunk would visibly freeze the app.
        /// </summary>
        [Fact]
        public async Task AcquireTimeout_FallsThroughUngated_RatherThanBlocking()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            var holder = Task.Run(() =>
            {
                using (SqliteWriteGate.EnterWrite("Test.Holder"))
                {
                    held.Set();
                    release.Wait(Generous);
                }
            });

            Assert.True(held.Wait(Generous), "holder never acquired the gate");

            var waited = Stopwatch.StartNew();
            bool acquiredWhileContended;
            using (SqliteWriteGate.EnterWrite("Test.Blocked", detail: null, timeoutMs: 100))
            {
                acquiredWhileContended = SqliteWriteGate.IsHeldByCurrentThread;
            }

            waited.Stop();

            release.Set();
            Assert.True(await CompletesAsync(holder));

            Assert.False(acquiredWhileContended, "expected a fail-soft fall-through, but the gate reported as held");
            Assert.True(
                waited.ElapsedMilliseconds < 5000,
                $"fall-through took {waited.ElapsedMilliseconds}ms — it must return promptly, not block");
        }

        /// <summary>
        /// A fall-through scope must not release a permit it never took, and must not corrupt the
        /// depth counter — otherwise the NEXT write on this thread would wrongly believe it is nested
        /// (skipping the gate) or would over-release the semaphore.
        /// </summary>
        [Fact]
        public async Task FallThroughScope_DoesNotCorruptDepth_ForSubsequentWrites()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            var holder = Task.Run(() =>
            {
                using (SqliteWriteGate.EnterWrite("Test.DepthHolder"))
                {
                    held.Set();
                    release.Wait(Generous);
                }
            });

            Assert.True(held.Wait(Generous));

            using (SqliteWriteGate.EnterWrite("Test.FellThrough", detail: null, timeoutMs: 50))
            {
                Assert.False(SqliteWriteGate.IsHeldByCurrentThread);
            }

            Assert.False(SqliteWriteGate.IsHeldByCurrentThread);

            release.Set();
            Assert.True(await CompletesAsync(holder));

            // The gate must still be acquirable normally afterwards.
            using (SqliteWriteGate.EnterWrite("Test.AfterFallThrough"))
            {
                Assert.True(SqliteWriteGate.IsHeldByCurrentThread);
            }

            Assert.False(SqliteWriteGate.IsHeldByCurrentThread);
        }

        /// <summary>
        /// The anti-starvation regression guard — the whole point of Phase 2. Reproduces the shape
        /// Phase 1 measured (CodeGraph.ChunkedWritePass: back-to-back short write transactions with
        /// 0–1ms gaps, ~99.6% duty cycle) and asserts a competing writer is still ADMITTED. Under the
        /// pre-Phase-2 model the competitor raced SQLite's unfair busy handler and could lose
        /// indefinitely; a queued gate bounds its wait to one in-flight write.
        ///
        /// Asserts admission (IsHeldByCurrentThread inside the scope), NOT merely that the call
        /// returned — a fail-soft fall-through also returns, and would mean starvation was NOT fixed.
        /// </summary>
        [Fact]
        public async Task HighDutyCycleWriter_DoesNotStarveCompetitor()
        {
            // A plain volatile flag, NOT a ManualResetEventSlim: a `using`-scoped event would be
            // DISPOSED when this method unwinds, and if the hog is still looping at that moment (the
            // path taken whenever the wait below times out) it would throw ObjectDisposedException on
            // a pool thread AND keep hammering the process-wide static gate, perturbing test classes
            // that xUnit runs in parallel. A flag cannot be disposed out from under the loop.
            var stop = 0;
            bool competitorAdmitted = false;

            // Mimic the starver: reacquire immediately after each release, leaving ~no gap.
            var hog = Task.Run(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    using (SqliteWriteGate.EnterWrite("Test.Hog"))
                    {
                        Thread.Sleep(5);
                    }
                }
            });

            var competitor = Task.Run(() =>
            {
                using (SqliteWriteGate.EnterWrite("Test.Competitor", detail: null, timeoutMs: 5000))
                {
                    competitorAdmitted = SqliteWriteGate.IsHeldByCurrentThread;
                }
            });

            bool finished = await CompletesAsync(competitor);
            Volatile.Write(ref stop, 1);

            // Awaited, not just signalled: a hog thread still hammering the static gate would leak into
            // sibling tests and make their acquires look contended.
            bool hogStopped = await CompletesAsync(hog);

            Assert.True(finished, "competitor never completed against a high-duty-cycle writer");
            Assert.True(hogStopped, "hog did not stop after being signalled");
            Assert.True(
                competitorAdmitted,
                "competitor was STARVED (fell through ungated) despite the gate — queued admission regressed");
        }

        /// <summary>
        /// Property 4 of the design: gate acquisition is co-extensive with the Phase 1 diagnostics
        /// census, so the gated set and the observed set cannot drift. A write that takes the gate
        /// must therefore appear in the busy dump as an active holder.
        /// </summary>
        [Fact]
        public void EnterWrite_RegistersWithContentionDiagnostics()
        {
            using (SqliteWriteGate.EnterWrite("Test.CensusOwner", "census-detail"))
            {
                string report = WriteContentionDiagnostics.BuildBusyReport("Test.Victim");
                Assert.Contains("Test.CensusOwner", report);
                Assert.Contains("census-detail", report);
            }

            string after = WriteContentionDiagnostics.BuildBusyReport("Test.Victim");
            Assert.DoesNotContain("Test.CensusOwner", after.Split("Writes completed")[0]);
        }

        [Fact]
        public void DoubleDispose_IsIdempotent_AndDoesNotOverRelease()
        {
            // Owner name must be UNIQUE across the whole test assembly: WriteContentionDiagnostics'
            // recent-write ring is static and process-wide, and WriteContentionDiagnosticsTests'
            // own double-dispose test asserts its owner appears in that ring EXACTLY once. Reusing
            // the bare name "Test.DoubleDispose" here silently failed that sibling test.
            var scope = SqliteWriteGate.EnterWrite("Test.GateDoubleDispose");
            Assert.True(SqliteWriteGate.IsHeldByCurrentThread);

            scope.Dispose();
            Assert.False(SqliteWriteGate.IsHeldByCurrentThread);

            // A second dispose must not release a second permit (which would let two writers in).
            scope.Dispose();
            Assert.False(SqliteWriteGate.IsHeldByCurrentThread);

            // Prove the gate still admits exactly one holder afterwards.
            using (SqliteWriteGate.EnterWrite("Test.GateAfterDoubleDispose"))
            {
                Assert.True(SqliteWriteGate.IsHeldByCurrentThread);
            }
        }

        /// <summary>
        /// Pipeline Run 1 regression guard (cross-model adversary, HIGH). The gate is only a fairness
        /// mechanism for writers that actually ENTER it, and the first Phase 2 pass gated the sites
        /// Phase 1 had instrumented — which did NOT include register_session, the ticket's own victim,
        /// because it happened not to fail during the Phase 1 capture.
        ///
        /// <para>This asserts the shape that was missing: a writer entering the gate while a
        /// high-duty-cycle holder is active gets ADMITTED (queued) rather than falling through ungated.
        /// The static census check (5) in verify-writegate.mjs pins the specific METHOD names; this
        /// test pins the BEHAVIOUR they depend on, so the guarantee survives both a code edit and a
        /// census edit.</para>
        /// </summary>
        [Fact]
        public async Task GatedWriter_IsAdmittedRatherThanFallingThrough_WhileStarverHolds()
        {
            var stop = 0;
            bool admitted = false;

            var starver = Task.Run(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    using (SqliteWriteGate.EnterWrite("Test.RegisterStarver"))
                    {
                        Thread.Sleep(5);
                    }
                }
            });

            // Stand-in for the register_session write unit (SaveSessionLineage / CloseOpenSessions).
            var register = Task.Run(() =>
            {
                using (SqliteWriteGate.EnterWrite("Test.RegisterSessionWrite", detail: null, timeoutMs: 5000))
                {
                    admitted = SqliteWriteGate.IsHeldByCurrentThread;
                }
            });

            bool finished = await CompletesAsync(register);
            Volatile.Write(ref stop, 1);
            bool starverStopped = await CompletesAsync(starver);

            Assert.True(finished, "the register-session-shaped write never completed against a starver");
            Assert.True(starverStopped, "starver did not stop after being signalled");
            Assert.True(
                admitted,
                "the register-session-shaped write FELL THROUGH UNGATED instead of being admitted — "
                + "queued admission regressed, which is the exact condition that leaves the ticket's "
                + "victim racing unfairly");
        }

        /// <summary>
        /// Pipeline Run 3 regression guard (debugger, HIGH). Reentrancy must key on SCOPE ENTRY, not on
        /// successful acquisition.
        ///
        /// <para>The shipped defect: <c>Depth</c> was incremented only when the acquire SUCCEEDED, so a
        /// fail-soft outer scope was invisible to nested <c>EnterWrite</c> calls. Each nested call then took
        /// a fresh acquire at its own budget. On the register path that turned one 2s admission into
        /// 2s + 10s + 10s — WORSE than before the wrap existed, and precisely on the contended branch the
        /// ticket is about. The "one admission" property was asserted by a census pin and a budget guard,
        /// but both only held on the success path, so nothing caught it.</para>
        ///
        /// <para>This asserts the property on the branch that actually broke: while a competitor holds the
        /// gate, an outer scope that FALLS THROUGH must still suppress a nested acquire.</para>
        /// </summary>
        [Fact]
        public async Task FallThroughOuterScope_StillSuppressesNestedAcquire()
        {
            var stop = 0;
            var holderReady = 0;
            bool nestedFellThroughToo = false;
            var nestedWait = TimeSpan.Zero;

            var holder = Task.Run(() =>
            {
                using (SqliteWriteGate.EnterWrite("Test.NestSuppressHolder"))
                {
                    // Signalled from INSIDE the scope: IsHeldByCurrentThread is thread-local, so the test
                    // thread cannot observe the holder's permit directly.
                    Volatile.Write(ref holderReady, 1);
                    while (Volatile.Read(ref stop) == 0) Thread.Sleep(5);
                }
            });

            var spun = Stopwatch.StartNew();
            while (Volatile.Read(ref holderReady) == 0 && spun.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            Assert.Equal(1, Volatile.Read(ref holderReady));

            var work = Task.Run(() =>
            {
                // Outer: short budget, WILL fall through because the holder owns the permit.
                using (SqliteWriteGate.EnterWrite("Test.NestOuter", detail: null, timeoutMs: 200))
                {
                    Assert.False(SqliteWriteGate.IsHeldByCurrentThread);

                    // Nested: a LONG budget. If reentrancy were keyed on acquisition, this would not pass
                    // through and would block for the full budget — the shipped defect. Timing it is what
                    // makes the assertion meaningful: a pass-through returns effectively instantly.
                    var nested = Stopwatch.StartNew();
                    using (SqliteWriteGate.EnterWrite("Test.NestInner", detail: null, timeoutMs: 10000))
                    {
                        nestedFellThroughToo = !SqliteWriteGate.IsHeldByCurrentThread;
                    }

                    nested.Stop();
                    nestedWait = nested.Elapsed;
                }
            });

            bool finished = await CompletesAsync(work);
            Volatile.Write(ref stop, 1);
            await CompletesAsync(holder);

            Assert.True(finished, "nested EnterWrite inside a fell-through scope did not complete");
            Assert.True(
                nestedWait < TimeSpan.FromMilliseconds(1000),
                $"nested EnterWrite waited {nestedWait.TotalMilliseconds:F0}ms inside an outer scope that had "
                + "already fallen through — it must PASS THROUGH, not take a second acquire. Re-attempting "
                + "cannot help: the outer already waited its full budget and gave up.");
            Assert.True(
                nestedFellThroughToo,
                "the nested scope reported holding the permit, but the outer scope never acquired one");
            Assert.False(SqliteWriteGate.IsHeldByCurrentThread);
        }

        [Fact]
        public void AcquireTimeout_DefaultExceedsLongestObservedHold()
        {
            // Phase 1's longest single hold was 3151ms (a 25-file code-graph chunk). The default
            // acquire budget must sit above it with headroom, so a fall-through means a genuine
            // anomaly rather than routine startup contention.
            const int longestObservedHoldMs = 3151;
            Assert.True(
                SqliteWriteGate.DefaultAcquireTimeoutMs > longestObservedHoldMs * 2,
                $"default acquire timeout ({SqliteWriteGate.DefaultAcquireTimeoutMs}ms) must clear the "
                + $"longest observed hold ({longestObservedHoldMs}ms) with margin");
        }
    }
}
