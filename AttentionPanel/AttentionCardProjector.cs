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

                    // Derived from the SAME decision as ObservedDetail, never computed alongside
                    // it. These two disagreeing is precisely the defect this flag exists to
                    // prevent: it would stamp "seen 8s ago" onto a question that was never an
                    // observation, reintroducing the fossil-as-live lie from the other direction.
                    DetailIsLive = !PrefersNotificationDetail(e),
                    ActivityAgeSeconds = e.LastActivityAtUtc is DateTime seen
                        ? Seconds(nowUtc - seen)
                        : -1,
                    SinceSeconds = Seconds(nowUtc - e.EnteredAtUtc),
                    BlockSeq = e.BlockSeq,
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
            => PrefersNotificationDetail(e) ? e.Detail : e.LastActivity;

        /// <summary>
        /// Whether the notification message should win over the live activity line.
        /// </summary>
        /// <remarks>
        /// Normally it should not, for the reason documented on <see cref="DetailFor"/>: a frozen
        /// sentence in the position of a live observation is how the owner read a 27-minute-old
        /// line as current.
        /// <para>
        /// A QUESTION BLOCK is the exception, and it is not the same mistake (task ee17f42d, live
        /// test 1). The notification message there is the question itself — the literal thing the
        /// owner is being asked to answer, and the whole reason the card is shouting. The last tool
        /// that ran before it is not an answer to "what do you want from me?"; the owner saw
        /// <c>Bash: HOOK=$(ls …)</c> on a card that was waiting for them to pick an option, and
        /// said so.
        /// </para>
        /// <para>
        /// The test is <see cref="AgentAttentionEntry.DetailIsQuestionText"/> and NOT the state
        /// (pipeline run 1, debugger). <see cref="AttentionState.BlockedQuestion"/> is reached from
        /// two raw types, and <c>elicitation_dialog</c>'s message is fixed boilerplate that merely
        /// restates the card's own verb. Keying on the state would trade a real observation for a
        /// sentence the reader has already read one line above — a worse card, arrived at by the
        /// same reasoning that makes this one better.
        /// </para>
        /// <para>
        /// It also is not a fossil in the sense that mattered there. <see cref="HasLiveActivity"/>
        /// stays false for it, so the view still labels it "from the alert, not observed since" —
        /// which on a blocked agent is exactly true: it has not done anything since asking. The
        /// honesty machinery is reused, not bypassed.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// The state test is an AND with the provenance test, not a thing the provenance test
        /// replaced (pipeline run 2, cross-model adversary). Provenance alone says the stored
        /// string IS a question; it does not say the agent is STILL waiting on it. A terminal that
        /// asked a question and then went <see cref="AttentionState.Offline"/> keeps both Detail
        /// and the flag — <c>MarkOffline</c> changes only the state — so a card reading
        /// "Disconnected" would have displaced its live activity line with a question nobody can
        /// answer any more.
        /// </remarks>
        private static bool PrefersNotificationDetail(AgentAttentionEntry e)
            => (e.State == AttentionState.BlockedQuestion
                && e.DetailIsQuestionText
                && !string.IsNullOrWhiteSpace(e.Detail))
               || !HasLiveActivity(e);

        private static long Seconds(TimeSpan span) =>
            span.Ticks <= 0 ? 0 : (long)span.TotalSeconds;

    }
}
