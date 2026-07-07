using System;

namespace MultiTerminal.Services.Startup
{
    /// <summary>
    /// Guards a one-time side effect (typically an event subscription on a shared,
    /// long-lived object) so it happens at most once across repeated attempts
    /// (task 4fec40e2).
    /// <para>
    /// The port-contention Retry loop re-invokes <c>MultiTerminalRestServer.StartAsync</c>,
    /// which re-runs its pre-bind wiring. Wiring that assigns (<c>broker.X = ...</c>) is
    /// naturally idempotent, but a <c>+=</c> subscription on the SHARED ProjectService would
    /// accumulate a duplicate handler on every retry and survive the eventual successful
    /// start. Routing that subscription through <see cref="Run"/> makes it fire once no
    /// matter how many times StartAsync is retried — and, being a tiny standalone type, lets
    /// a unit test falsify the guarantee with a real handler-count assertion instead of
    /// binding :5050.
    /// </para>
    /// </summary>
    internal sealed class OneTimeHook
    {
        private bool _done;

        /// <summary>True once the guarded action has run.</summary>
        public bool HasRun => _done;

        /// <summary>
        /// Run <paramref name="action"/> only if it has not run before on this instance.
        /// Returns true if it ran this call, false if it was already done.
        /// </summary>
        public bool Run(Action action)
        {
            if (_done)
            {
                return false;
            }

            action();
            _done = true;
            return true;
        }
    }
}
