using System.Threading;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Decides, once, that a headless gloss writer should be stopped (ticket f4992532).
    /// </summary>
    /// <remarks>
    /// Two things can end a writer: its turn finishing (the stream's <c>result</c> message) and the
    /// time limit backstop. Both call <see cref="TryClaimStop"/>; only the first caller gets
    /// <c>true</c>, so StopAsync runs once whichever fires first. Pure and thread-safe: the turn-end
    /// message arrives on the stdout reader thread and the backstop on a timer thread.
    /// </remarks>
    internal sealed class GlossWriterStopGate
    {
        /// <summary>Same as the per-ticket gloss cooldown default (GlossBackfillService), 10 minutes.</summary>
        internal const int BackstopMs = 10 * 60 * 1000;

        private int _claimed;

        /// <summary>True for exactly one caller, ever.</summary>
        internal bool TryClaimStop() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;

        /// <summary>True once a stop has been claimed.</summary>
        internal bool StopClaimed => Volatile.Read(ref _claimed) == 1;

        /// <summary>The stream-json message that marks the end of a turn.</summary>
        internal static bool IsTurnEnd(AgentMessage message) =>
            message != null && message.Type == AgentMessageType.Result;
    }
}
