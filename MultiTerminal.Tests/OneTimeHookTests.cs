using System;
using MultiTerminal.Services.Startup;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Falsifiable evidence for the transactional-retry fix (task 4fec40e2, Cross-Model
    /// Adversary HIGH). The port-contention Retry loop re-invokes MultiTerminalRestServer.StartAsync,
    /// which re-runs its pre-bind wiring. The subscription to the SHARED ProjectService.ProjectRegistered
    /// event is routed through <see cref="OneTimeHook"/> so N retries attach ONE handler, not N.
    /// These tests drive a real event source and assert the HANDLER-INVOCATION COUNT — remove the
    /// guard and the count becomes N, so the guarantee is genuinely falsifiable.
    /// </summary>
    public class OneTimeHookTests
    {
        private sealed class FakeEventSource
        {
            public event EventHandler Fired;

            public void Raise() => Fired?.Invoke(this, EventArgs.Empty);
        }

        [Fact]
        public void Subscription_through_hook_fires_once_across_many_retries()
        {
            var source = new FakeEventSource();
            var hook = new OneTimeHook();
            int handlerInvocations = 0;

            // Simulate 5 StartAsync attempts (repeated bind-failure Retries + eventual success).
            for (int attempt = 0; attempt < 5; attempt++)
            {
                hook.Run(() => source.Fired += (s, e) => handlerInvocations++);
            }

            source.Raise();

            // Exactly one handler was attached ⇒ one invocation. WITHOUT the guard this is 5.
            Assert.Equal(1, handlerInvocations);
            Assert.True(hook.HasRun);
        }

        [Fact]
        public void Run_executes_once_and_reports_status()
        {
            var hook = new OneTimeHook();
            int count = 0;

            Assert.True(hook.Run(() => count++));   // first call runs
            Assert.False(hook.Run(() => count++));  // subsequent calls are no-ops
            Assert.False(hook.Run(() => count++));
            Assert.Equal(1, count);
        }
    }
}
