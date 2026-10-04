using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.AttentionPanel
{
    /// <summary>
    /// What one agent is to its team, as the Attention rail shows it (task d71bb221).
    /// </summary>
    public sealed class AttentionTeamLink
    {
        /// <summary><see cref="AttentionTeamRoles.Pm"/> or <see cref="AttentionTeamRoles.Helper"/>.</summary>
        public string Role { get; set; }

        /// <summary>
        /// For a helper, the agent name of the PM whose pane spawned it, or null when that is not
        /// known. Never guessed: null puts the helper in the "PM unknown" group.
        /// </summary>
        public string Pm { get; set; }
    }

    /// <summary>The role strings the card carries. They are part of the view contract.</summary>
    public static class AttentionTeamRoles
    {
        /// <summary>A Project Manager: the Owner opened this terminal on a project.</summary>
        public const string Pm = "pm";

        /// <summary>Started by <c>spawn_helper</c> from another pane.</summary>
        public const string Helper = "helper";
    }

    /// <summary>
    /// The facts about one terminal pane that decide its team role (task d71bb221). Gathered by
    /// MainForm from the dock; kept as plain data so the decision can be tested without a pane.
    /// </summary>
    public sealed class AttentionPaneTeamFacts
    {
        /// <summary>The pane's stable key, which survives an MT restart (unlike its DocId).</summary>
        public string PaneKey { get; set; }

        /// <summary>The agent the pane runs, as the card is keyed.</summary>
        public string AgentName { get; set; }

        /// <summary>The pane's tab currently shows "(PM)".</summary>
        public bool DisplayedAsPm { get; set; }

        /// <summary>The pane's CURRENT launch was a <c>spawn_helper</c> spawn.</summary>
        public bool LaunchedAsHelper { get; set; }

        /// <summary>The agent name the pane's helper link was recorded under, or null for no link.</summary>
        public string LinkHelperName { get; set; }

        /// <summary>The spawner pane's <see cref="PaneKey"/> from the link, or null when unproven.</summary>
        public string LinkSpawnerPaneKey { get; set; }
    }

    /// <summary>
    /// Decides each agent's team role from the panes (task d71bb221).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The PM is found by PANE, never by a typed name.</b> A helper's link records the spawner
    /// pane's key, which MT stamped from the nonce-proven spawner in <c>SpawnedPaneRegistry</c>.
    /// The spawner NAME a caller passes to <c>spawn_helper</c> is whatever it typed, so trusting it
    /// would let any agent file itself under any PM.
    /// </para>
    /// <para>
    /// <b>A link holds only while the pane runs under the name it was recorded for.</b> That is what
    /// lets it survive an MT restart (the Owner relaunches the restored pane as the same agent) while
    /// dropping it when the pane is reused for somebody else.
    /// </para>
    /// <para>
    /// Helper is checked before PM, the same precedence as <c>TerminalRoles.Resolve</c>.
    /// </para>
    /// </remarks>
    public static class AttentionTeamResolver
    {
        /// <summary>
        /// One link per agent name, first pane wins (the caller passes running panes first, the same
        /// order it uses for the project line). Agents with no role get no entry.
        /// </summary>
        public static Dictionary<string, AttentionTeamLink> Resolve(IEnumerable<AttentionPaneTeamFacts> panes)
        {
            var result = new Dictionary<string, AttentionTeamLink>(StringComparer.OrdinalIgnoreCase);
            if (panes == null) return result;

            var all = panes.Where(p => p != null).ToList();
            foreach (var pane in all)
            {
                string agent = pane.AgentName;
                if (string.IsNullOrWhiteSpace(agent) || result.ContainsKey(agent)) continue;

                bool linkHolds = !string.IsNullOrEmpty(pane.LinkHelperName)
                                 && string.Equals(pane.LinkHelperName, agent, StringComparison.OrdinalIgnoreCase);

                if (linkHolds || pane.LaunchedAsHelper)
                {
                    string pm = null;
                    if (linkHolds && !string.IsNullOrEmpty(pane.LinkSpawnerPaneKey))
                    {
                        // Ordinal: the key is a minted id, exact by decision.
                        var spawner = all.FirstOrDefault(p => !ReferenceEquals(p, pane)
                            && string.Equals(p.PaneKey, pane.LinkSpawnerPaneKey, StringComparison.Ordinal));
                        if (!string.IsNullOrWhiteSpace(spawner?.AgentName)
                            && !string.Equals(spawner.AgentName, agent, StringComparison.OrdinalIgnoreCase))
                        {
                            pm = spawner.AgentName;
                        }
                    }

                    result[agent] = new AttentionTeamLink { Role = AttentionTeamRoles.Helper, Pm = pm };
                }
                else if (pane.DisplayedAsPm)
                {
                    result[agent] = new AttentionTeamLink { Role = AttentionTeamRoles.Pm };
                }
            }

            return result;
        }
    }

    /// <summary>One group of the By-team order, as the view consumes it (task d71bb221).</summary>
    /// <remarks>
    /// ⚠️ The JSON names are a contract no compiler checks, exactly as on <see cref="AttentionCard"/>,
    /// and are pinned the same way by <c>AttentionTeamLayoutTests</c>.
    /// </remarks>
    public sealed class AttentionTeamGroup
    {
        /// <summary>
        /// <see cref="AttentionTeamLayout.KindTeam"/> (a head card with helpers),
        /// <see cref="AttentionTeamLayout.KindSolo"/> (a head card alone),
        /// <see cref="AttentionTeamLayout.KindHeadless"/> (helpers whose PM is known but has no card)
        /// or <see cref="AttentionTeamLayout.KindUnknown"/> (helpers whose PM is unknown).
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }

        /// <summary>The head card's session id, or null for the two headless kinds.</summary>
        [JsonPropertyName("headId")]
        public string HeadId { get; set; }

        /// <summary>The PM's name for a headless group, else null.</summary>
        [JsonPropertyName("pmName")]
        public string PmName { get; set; }

        /// <summary>Helper session ids, in display order (blocked first).</summary>
        [JsonPropertyName("helperIds")]
        public List<string> HelperIds { get; set; } = new List<string>();

        /// <summary>The longest-waiting blocked helper, for the PM card's badge, or null.</summary>
        [JsonPropertyName("needsYou")]
        public AttentionNeedsYou NeedsYou { get; set; }
    }

    /// <summary>"Uma needs you · 2m" on a PM card.</summary>
    public sealed class AttentionNeedsYou
    {
        /// <summary>The blocked helper's session id, so the badge can read the helper's ticking age.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>The blocked helper's name.</summary>
        [JsonPropertyName("agent")]
        public string Agent { get; set; }

        /// <summary>How long it has been blocked.</summary>
        [JsonPropertyName("sinceSeconds")]
        public long SinceSeconds { get; set; }

        /// <summary>How many OTHER helpers in the team are blocked too.</summary>
        [JsonPropertyName("more")]
        public int More { get; set; }
    }

    /// <summary>
    /// The By-team order (task d71bb221): each PM with its helpers underneath.
    /// </summary>
    /// <remarks>
    /// <para>Pure and static, like <see cref="AttentionCardProjector"/>, so every rule is testable.</para>
    /// <para>
    /// The rules, which are the Owner-approved design:
    /// </para>
    /// <list type="bullet">
    /// <item>If ANYONE in a team is blocked, the whole team moves to the top. Several blocked teams:
    /// the one waiting longest goes first — the same rule as single cards in Attention first.</item>
    /// <item>A blocked helper sorts first among its team's helpers, and the PM card names it.</item>
    /// <item>Helpers whose PM is unknown go in their own group at the bottom (unless blocked —
    /// blocked-first holds in every order). A PM is never guessed.</item>
    /// <item>Agents with no role are a group of one, ordered exactly as Attention first orders them.</item>
    /// </list>
    /// <para>
    /// Helpers of helpers are filed under the first ancestor that is not a helper, so a team is
    /// always one level deep. A chain that loops, or ends at an agent with no card, is not followed
    /// further than it can be proven.
    /// </para>
    /// </remarks>
    public static class AttentionTeamLayout
    {
        /// <summary>A head card with one or more helpers.</summary>
        public const string KindTeam = "team";

        /// <summary>A head card alone (a PM with no helpers, or an agent with no role).</summary>
        public const string KindSolo = "solo";

        /// <summary>Helpers whose PM is known by name but has no card on the rail.</summary>
        public const string KindHeadless = "headless";

        /// <summary>Helpers whose PM is unknown.</summary>
        public const string KindUnknown = "unknown";

        private sealed class Work
        {
            public AttentionCard Head;
            public string PmName;
            public string Kind;
            public int FirstIndex;
            public readonly List<(AttentionCard Card, int Index)> Helpers = new List<(AttentionCard, int)>();
        }

        /// <summary>Builds the ordered groups. Every card appears in exactly one group.</summary>
        public static List<AttentionTeamGroup> Build(IReadOnlyList<AttentionCard> cards)
        {
            var groups = new List<AttentionTeamGroup>();
            if (cards == null || cards.Count == 0) return groups;

            // Heads: the first non-helper card per agent name.
            var headByAgent = new Dictionary<string, (AttentionCard Card, int Index)>(StringComparer.OrdinalIgnoreCase);
            var helperByAgent = new Dictionary<string, AttentionCard>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null || string.IsNullOrWhiteSpace(c.Agent)) continue;
                if (IsHelper(c))
                {
                    if (!helperByAgent.ContainsKey(c.Agent)) helperByAgent[c.Agent] = c;
                }
                else if (!headByAgent.ContainsKey(c.Agent))
                {
                    headByAgent[c.Agent] = (c, i);
                }
            }

            var work = new List<Work>();
            var byHead = new Dictionary<AttentionCard, Work>();
            var headless = new Dictionary<string, Work>(StringComparer.OrdinalIgnoreCase);
            Work unknown = null;

            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null) continue;

                if (!IsHelper(c))
                {
                    var w = new Work { Head = c, Kind = KindSolo, FirstIndex = i };
                    work.Add(w);
                    byHead[c] = w;
                }
            }

            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                if (c == null || !IsHelper(c)) continue;

                string pm = RootPm(c, headByAgent, helperByAgent, out bool hasCard);
                Work target;
                if (pm == null)
                {
                    if (unknown == null)
                    {
                        unknown = new Work { Kind = KindUnknown, FirstIndex = i };
                        work.Add(unknown);
                    }
                    target = unknown;
                }
                else if (hasCard)
                {
                    target = byHead[headByAgent[pm].Card];
                    target.Kind = KindTeam;
                }
                else
                {
                    if (!headless.TryGetValue(pm, out target))
                    {
                        target = new Work { Kind = KindHeadless, PmName = pm, FirstIndex = i };
                        headless[pm] = target;
                        work.Add(target);
                    }
                }

                target.Helpers.Add((c, i));
            }

            foreach (var w in work)
            {
                w.Helpers.Sort((a, b) => CompareCards(a.Card, a.Index, b.Card, b.Index));
            }

            work.Sort(CompareGroups);

            foreach (var w in work)
            {
                var g = new AttentionTeamGroup
                {
                    Kind = w.Kind,
                    HeadId = w.Head?.Id,
                    PmName = w.PmName,
                    HelperIds = w.Helpers.Select(h => h.Card.Id).ToList(),
                };

                var blockedHelpers = w.Helpers.Where(h => IsBlocked(h.Card)).ToList();
                if (w.Head != null && blockedHelpers.Count > 0)
                {
                    // Helpers are already sorted blocked-first, longest wait first.
                    var first = blockedHelpers[0].Card;
                    g.NeedsYou = new AttentionNeedsYou
                    {
                        Id = first.Id,
                        Agent = first.Agent,
                        SinceSeconds = first.SinceSeconds,
                        More = blockedHelpers.Count - 1,
                    };
                }

                groups.Add(g);
            }

            return groups;
        }

        private static bool IsHelper(AttentionCard c)
            => string.Equals(c?.Role, AttentionTeamRoles.Helper, StringComparison.Ordinal);

        /// <summary>The card states the rail treats as "needs you". Mirrors the view's BLOCKING map.</summary>
        internal static bool IsBlocked(AttentionCard c)
            => c != null
               && (c.State == nameof(AttentionState.BlockedQuestion)
                   || c.State == nameof(AttentionState.BlockedPermission)
                   || c.State == nameof(AttentionState.BlockedUnknown));

        /// <summary>Attention first's rank: blocked, then everyone else, then offline.</summary>
        private static int Rank(AttentionCard c)
            => IsBlocked(c) ? 0 : (c.State == nameof(AttentionState.Offline) ? 2 : 1);

        /// <summary>The Attention-first comparison for single cards: rank, longest in state, input order.</summary>
        private static int CompareCards(AttentionCard a, int ia, AttentionCard b, int ib)
        {
            int r = Rank(a).CompareTo(Rank(b));
            if (r != 0) return r;
            int s = b.SinceSeconds.CompareTo(a.SinceSeconds);
            if (s != 0) return s;
            return ia.CompareTo(ib);
        }

        private static IEnumerable<AttentionCard> Members(Work w)
        {
            if (w.Head != null) yield return w.Head;
            foreach (var h in w.Helpers) yield return h.Card;
        }

        private static int CompareGroups(Work a, Work b)
        {
            long waitA = BlockedWait(a), waitB = BlockedWait(b);
            bool blockedA = waitA >= 0, blockedB = waitB >= 0;

            // 1. Any blocked member lifts the whole group; the longest wait goes first.
            if (blockedA != blockedB) return blockedA ? -1 : 1;
            if (blockedA)
            {
                int w = waitB.CompareTo(waitA);
                return w != 0 ? w : a.FirstIndex.CompareTo(b.FirstIndex);
            }

            // 2. Calm groups without a head (PM unknown / PM has no card) go to the bottom.
            int tailA = TailRank(a), tailB = TailRank(b);
            if (tailA != tailB) return tailA.CompareTo(tailB);
            if (a.Head == null) return a.FirstIndex.CompareTo(b.FirstIndex);

            // 3. Headed groups: Attention first's rule on the head, except that a group is "live"
            //    when any member is, so a working helper keeps an offline PM's team up with the rest.
            int ra = Members(a).Min(Rank), rb = Members(b).Min(Rank);
            if (ra != rb) return ra.CompareTo(rb);
            int s = b.Head.SinceSeconds.CompareTo(a.Head.SinceSeconds);
            return s != 0 ? s : a.FirstIndex.CompareTo(b.FirstIndex);
        }

        /// <summary>0 for a group with a head, 1 for "Helpers of X", 2 for "PM unknown" (the very bottom).</summary>
        private static int TailRank(Work w)
            => w.Head != null ? 0 : (w.Kind == KindHeadless ? 1 : 2);

        /// <summary>The longest wait of any blocked member, or -1 when nobody is blocked.</summary>
        private static long BlockedWait(Work w)
        {
            long best = -1;
            foreach (var c in Members(w))
            {
                if (IsBlocked(c) && c.SinceSeconds > best) best = c.SinceSeconds;
            }

            return best;
        }

        /// <summary>
        /// The first ancestor of a helper that is not itself a helper. Returns its name and whether
        /// it has a card; null when the chain cannot be followed to a named agent (no PM recorded, or
        /// a loop).
        /// </summary>
        private static string RootPm(
            AttentionCard helper,
            Dictionary<string, (AttentionCard Card, int Index)> headByAgent,
            Dictionary<string, AttentionCard> helperByAgent,
            out bool hasCard)
        {
            hasCard = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { helper.Agent ?? string.Empty };
            string name = helper.Pm;
            while (!string.IsNullOrWhiteSpace(name))
            {
                if (headByAgent.ContainsKey(name))
                {
                    hasCard = true;
                    return name;
                }

                if (!helperByAgent.TryGetValue(name, out var parent))
                {
                    return name;   // named, proven, but not on the rail
                }

                if (!seen.Add(name)) return null;   // a loop proves nothing
                name = parent.Pm;
            }

            return null;
        }
    }
}
