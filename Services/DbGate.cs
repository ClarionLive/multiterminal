using System;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// A per-connection serialization gate (ticket bb2b0104). Each connection-owning class holds
    /// ONE private <see cref="DbGate"/> and wraps every connection-touching method body in
    /// <c>using var gate = _gate.Enter();</c> as its first statement. This is the same reentrant-Monitor
    /// scope-guard idiom ad08caac introduced inline on TaskDatabase (<c>LockConn()</c> / <c>LockHandle</c>),
    /// factored into one audited type so the sibling owners (KnowledgeDatabase, CodeGraphDatabase,
    /// SessionMemoryDatabase, BranchMetadataService, OwnerProfileService, SourceControlAccountService)
    /// share it instead of copy-pasting the struct six times.
    ///
    /// <para>Reentrant on purpose: the underlying <see cref="Monitor"/> lets the SAME thread re-acquire,
    /// so a gated method may call another gated method on the same instance without self-deadlock — the
    /// reason ad08caac chose Monitor over a non-reentrant SemaphoreSlim. It serializes only THIS class's
    /// access to ITS OWN connection; WAL + busy_timeout handle contention between the separate connections
    /// different owners hold.</para>
    ///
    /// <para><b>Footgun:</b> the return value MUST be captured in a <c>using</c> local. Calling
    /// <c>_gate.Enter()</c> without a <c>using</c> acquires the lock and never releases it (deadlock).
    /// Not for async/iterator methods — a lock must not be held across an await/yield boundary.</para>
    /// </summary>
    internal sealed class DbGate
    {
        private readonly object _lock = new object();

        /// <summary>Acquires the gate. Dispose the returned handle (via <c>using</c>) to release it.</summary>
        public Handle Enter()
        {
            Monitor.Enter(_lock);
            return new Handle(_lock);
        }

        /// <summary>Scope guard returned by <see cref="Enter"/>; releases the gate on dispose.</summary>
        public readonly struct Handle : IDisposable
        {
            private readonly object _lock;

            public Handle(object @lock)
            {
                _lock = @lock;
            }

            public void Dispose()
            {
                Monitor.Exit(_lock);
            }
        }
    }
}
