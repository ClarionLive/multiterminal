using System;
using System.Threading;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Decides whether a terminal event should send Oracle its once-per-app-session bootstrap
    /// (ticket 0ff1b520, item 15).
    /// </summary>
    /// <remarks>
    /// <para>Two triggers now lead here: the channel server's port report (the original one, until
    /// item 16 removes it) and native messaging credentials arriving. Both are raised on REST
    /// thread-pool threads, and for one Oracle startup both normally fire, so the old
    /// <c>if (!flag) { flag = true; ... }</c> check-then-set could let each thread pass the check
    /// and send the bootstrap twice — which is two digest runs and duplicate suggestion tasks. The
    /// claim is a single atomic exchange.</para>
    /// <para>A failed delivery <see cref="Release"/>s the claim, so the other trigger can still
    /// deliver. That keeps "at most one bootstrap delivered" rather than "at most one attempted",
    /// which matters while the two routes differ: native injection has an off switch, and the
    /// channel has no port until the channel server reports one.</para>
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
