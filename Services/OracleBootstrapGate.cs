using System;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Decides whether a terminal event should send Oracle its once-per-app-session bootstrap
    /// (ticket 0ff1b520, item 15).
    /// </summary>
    /// <remarks>
    /// <para>The trigger is native messaging credentials arriving, raised on a REST thread-pool
    /// thread. Item 15 briefly ran it alongside the channel server's port report, and two triggers
    /// on two threads made the old <c>if (!flag) { flag = true; ... }</c> check-then-set able to send
    /// the bootstrap twice — two digest runs and duplicate suggestion tasks. Item 16 removed the
    /// channel trigger; the claim stays a single atomic exchange because a /clear or restart can
    /// post credentials again while a first bootstrap is still in flight.</para>
    /// <para>A failed delivery <see cref="Release"/>s the claim, so Oracle's next credential post can
    /// try again. That keeps "at most one bootstrap delivered" rather than "at most one attempted".</para>
    /// </remarks>
    public sealed class OracleBootstrapGate
    {
        private int _claimed;

        /// <summary>Whether the bootstrap has been claimed and not released.</summary>
        public bool IsClaimed => Volatile.Read(ref _claimed) == 1;

        /// <summary>
        /// True exactly once for Oracle's name (case-insensitive, as every Oracle name check in MT is)
        /// until <see cref="Release"/>; false for any other name, which never consumes the claim.
        /// </summary>
        public bool TryClaim(string terminalName)
        {
            if (!string.Equals(terminalName, OracleService.OracleName, StringComparison.OrdinalIgnoreCase))
                return false;

            return Interlocked.Exchange(ref _claimed, 1) == 0;
        }

        /// <summary>Gives the claim back after a delivery that did not arrive.</summary>
        public void Release() => Volatile.Write(ref _claimed, 0);
    }
}
