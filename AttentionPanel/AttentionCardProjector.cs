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
        /// <param name="agentProjects">
        /// FALLBACK project name by agent name, derived from the agent's claimed task. Optional.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Why a fallback exists at all (task 42052f0c).</b> <see cref="AgentAttentionEntry.Project"/>
        /// is written in exactly one place — <c>ApplyNotification</c>, from the hook's
        /// <c>project_name</c>. So a terminal that simply works and never blocks has no code path
        /// that ever learns its project, and the card's project line was blank for most terminals
        /// most of the time. That is the same defect shape edcdcdd5 fixed for <c>Working</c>: the
        /// field existed, the view rendered it, and nothing called the writer.
        /// </para>
        /// <para>
        /// The observed value WINS. This is a fallback, never an override: what the hook read off
        /// disk is ground truth about where the agent is actually running, while the claimed task's
        /// project is an inference about where it is working. When they disagree, the observation is
        /// the one to trust — and an agent legitimately can hold a ticket in one project while
        /// running in another.
        /// </para>
        /// </remarks>
        public static List<AttentionCard> Project(
            IEnumerable<AgentAttentionEntry> entries,
            IReadOnlyDictionary<string, string> agentColors,
            IReadOnlyDictionary<string, AttentionTicketClaim> claims,
            DateTime nowUtc,
            IReadOnlyDictionary<string, string> agentProjects = null)
        {
            var cards = new List<AttentionCard>();
            if (entries == null) return cards;

            foreach (var e in entries)
            {
                if (e == null) continue;

                string agent = e.AgentName;
                AttentionTicketClaim claim = null;
                string color = null;
                string fallbackProject = null;

                if (!string.IsNullOrWhiteSpace(agent))
                {
                    if (claims != null) claims.TryGetValue(agent, out claim);
                    if (agentColors != null) agentColors.TryGetValue(agent, out color);
                    if (agentProjects != null) agentProjects.TryGetValue(agent, out fallbackProject);
                }

                // Observed beats inferred — see the remarks on Project().
                string project = string.IsNullOrWhiteSpace(e.Project) ? fallbackProject : e.Project;

                cards.Add(new AttentionCard
                {
                    Id = e.SessionId,
                    Agent = agent,
                    Color = string.IsNullOrWhiteSpace(color) ? DefaultColor : color,
                    Project = project,
                    State = e.State.ToString(),
                    ObservedVerb = Verb(e.State),
                    ObservedDetail = DetailFor(e),
                    DetailIsLive = HasLiveActivity(e),
                    ActivityAgeSeconds = e.LastActivityAtUtc is DateTime seen
                        ? Seconds(nowUtc - seen)
                        : -1,
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

        /// <summary>Whether this entry has a live observation to show (task edcdcdd5).</summary>
        private static bool HasLiveActivity(AgentAttentionEntry e)
            => !string.IsNullOrWhiteSpace(e.LastActivity) && e.LastActivityAtUtc.HasValue;

        /// <summary>
        /// The detail line: the LIVE activity when there is one, else the notification message.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="AgentAttentionEntry.Detail"/> is the notification text captured at block time
        /// and never regenerated — Claude Code's own "Alice is waiting for your input". Rendering it
        /// in the position of a live observation is what let the owner read a 27-minute-old sentence
        /// as a current one. Live activity wins whenever it exists.
        /// </para>
        /// <para>
        /// The notification message is kept as the FALLBACK rather than dropped, because before any
        /// tool has run it is the only thing observed about that session and is genuinely true at
        /// that moment. <see cref="AttentionCard.DetailIsLive"/> is what tells the two apart, so the
        /// view can mark the fossil rather than the reader having to guess.
        /// </para>
        /// </remarks>
        private static string DetailFor(AgentAttentionEntry e)
            => HasLiveActivity(e) ? e.LastActivity : e.Detail;

        private static long Seconds(TimeSpan span) =>
            span.Ticks <= 0 ? 0 : (long)span.TotalSeconds;
    }
}
