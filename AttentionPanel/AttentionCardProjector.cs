using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.AttentionPanel
{
    /// <summary>
    /// Turns attention state plus board claims into the cards the panel renders
    /// (task 2289bb8a item 5).
    /// </summary>
    /// <remarks>
    /// Pure and static on purpose: it takes prepared inputs rather than reaching for the broker,
    /// so the projection can be tested without a database, a WebView2 or a running app — which
    /// matters here because an agent inside MultiTerminal cannot deploy, and the panel itself can
    /// only ever be exercised by the owner.
    /// </remarks>
    public static class AttentionCardProjector
    {
        /// <summary>Fallback avatar colour when a terminal has no registered colour.</summary>
        private const string DefaultColor = "#89b4fa";

        /// <summary>
        /// Builds the card list.
        /// </summary>
        /// <param name="entries">Observed attention state, one per session.</param>
        /// <param name="agentColors">Avatar colour by agent name. Missing is fine.</param>
        /// <param name="claims">Board claim by agent name. Missing means "claims nothing".</param>
        /// <param name="nowUtc">Clock, injected so ages are deterministic under test.</param>
        public static List<AttentionCard> Project(
            IEnumerable<AgentAttentionEntry> entries,
            IReadOnlyDictionary<string, string> agentColors,
            IReadOnlyDictionary<string, AttentionTicketClaim> claims,
            DateTime nowUtc)
        {
            var cards = new List<AttentionCard>();
            if (entries == null) return cards;

            foreach (var e in entries)
            {
                if (e == null) continue;

                string agent = e.AgentName;
                AttentionTicketClaim claim = null;
                string color = null;

                if (!string.IsNullOrWhiteSpace(agent))
                {
                    if (claims != null) claims.TryGetValue(agent, out claim);
                    if (agentColors != null) agentColors.TryGetValue(agent, out color);
                }

                cards.Add(new AttentionCard
                {
                    Id = e.SessionId,
                    Agent = agent,
                    Color = string.IsNullOrWhiteSpace(color) ? DefaultColor : color,
                    Project = e.Project,
                    State = e.State.ToString(),
                    ObservedVerb = Verb(e.State),
                    ObservedDetail = e.Detail,
                    SinceSeconds = Seconds(nowUtc - e.EnteredAtUtc),
                    TicketId = claim?.TaskId,
                    TicketItem = claim?.ItemLabel,
                    ClaimAgeSeconds = claim == null ? 0 : Seconds(nowUtc - claim.UpdatedAtUtc),
                });
            }

            return cards;
        }

        /// <summary>
        /// The observed headline for a state.
        /// </summary>
        /// <remarks>
        /// Written from the owner's side of the screen: what is happening TO THEM, not what the
        /// system recorded. "Needs permission" rather than "permission_prompt received".
        /// <para>
        /// <see cref="AttentionState.Unknown"/> says nothing has been OBSERVED — it must never read
        /// as "idle" or "fine". Ambient notifications are not persisted while remote mode is off,
        /// so after a restart every session legitimately starts here, and calling that "idle" would
        /// state as fact the one thing the panel does not know.
        /// </para>
        /// </remarks>
        private static string Verb(AttentionState state)
        {
            switch (state)
            {
                case AttentionState.BlockedQuestion: return "Asked you a question";
                case AttentionState.BlockedPermission: return "Needs permission";
                case AttentionState.BlockedUnknown: return "Waiting on you";
                case AttentionState.Working: return "Working";
                case AttentionState.Idle: return "Finished and idle";
                case AttentionState.Offline: return "Disconnected";
                default: return "Nothing observed yet";
            }
        }

        private static long Seconds(TimeSpan span) =>
            span.Ticks <= 0 ? 0 : (long)span.TotalSeconds;
    }
}
