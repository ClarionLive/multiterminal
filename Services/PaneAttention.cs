using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.Services
{
    /// <summary>
    /// What a terminal pane shows in its header and on its tab while its agent needs the Owner
    /// (task 51a8a17b). The Attention panel lists every session; with several panes open the Owner
    /// still has to find which pane that is, so the pane itself says so.
    /// </summary>
    public enum PaneAttentionLevel
    {
        None = 0,

        /// <summary>The agent finished its turn and it is the Owner's move (soft).</summary>
        YourTurn,

        /// <summary>Blocked on a permission prompt (or a block of unknown flavour).</summary>
        Permission,

        /// <summary>Blocked on a question, in a dialog or in chat.</summary>
        Question,
    }

    /// <summary>
    /// Pure: which attention level each pane shows, and its colours. No UI, no broker.
    /// </summary>
    public static class PaneAttention
    {
        /// <summary>One pane, as the matcher needs it.</summary>
        public sealed class PaneInfo
        {
            public string PaneId { get; set; }

            /// <summary>The Claude session the pane holds, or null before it is learned.</summary>
            public string SessionId { get; set; }

            /// <summary>
            /// The pane's identity: its confirmed agent name (<c>TerminalDocument.OriginalAgentName</c>),
            /// or its tab title while it has none, as <c>ClaudeProfilePaneLookup.Match</c> decides it.
            /// </summary>
            public string AgentName { get; set; }

            /// <summary>A spawned helper: its turn end is for its PM, not the Owner.</summary>
            public bool IsHelper { get; set; }

            /// <summary>
            /// A launch is running in the pane. A pane whose process exited, or that sits on its start
            /// screen, shows nothing: an entry left over from its last agent must not colour it.
            /// </summary>
            public bool IsLive { get; set; } = true;
        }

        /// <summary>
        /// The level an attention state gives a pane. A helper's <see cref="AttentionState.YourTurn"/>
        /// is None, as on its Attention card (task 3d51315b); its blocks still show.
        /// </summary>
        public static PaneAttentionLevel LevelFor(AttentionState state, bool isHelper)
        {
            switch (state)
            {
                case AttentionState.BlockedQuestion: return PaneAttentionLevel.Question;
                case AttentionState.BlockedPermission:
                case AttentionState.BlockedUnknown: return PaneAttentionLevel.Permission;
                case AttentionState.YourTurn: return isHelper ? PaneAttentionLevel.None : PaneAttentionLevel.YourTurn;
                default: return PaneAttentionLevel.None;
            }
        }

        /// <summary>
        /// The level for every pane (panes with nothing to show are included as None, so a caller can
        /// clear them). An entry goes to the pane holding its session id. An entry whose session no
        /// pane holds goes, by agent name, to the one pane with that name that has no session id yet;
        /// two such panes is ambiguous and neither is coloured, because colouring the wrong pane
        /// sends the Owner to the wrong agent. Names compare as the broker compares terminal names
        /// (ordinal-ignore-case, untrimmed). A pane with several entries shows the strongest.
        /// </summary>
        public static Dictionary<string, PaneAttentionLevel> Assign(
            IEnumerable<PaneInfo> panes, IEnumerable<AgentAttentionEntry> entries)
        {
            var result = new Dictionary<string, PaneAttentionLevel>(StringComparer.Ordinal);
            var bySession = new Dictionary<string, PaneInfo>(StringComparer.OrdinalIgnoreCase);
            var unsessionedByName = new Dictionary<string, List<PaneInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in panes ?? Array.Empty<PaneInfo>())
            {
                if (p?.PaneId == null) continue;
                result[p.PaneId] = PaneAttentionLevel.None;
                if (!p.IsLive) continue;

                if (!string.IsNullOrEmpty(p.SessionId))
                {
                    // Two panes claiming one session: neither can be trusted with it.
                    if (bySession.ContainsKey(p.SessionId)) bySession[p.SessionId] = null;
                    else bySession[p.SessionId] = p;
                }
                else if (!string.IsNullOrEmpty(p.AgentName))
                {
                    if (!unsessionedByName.TryGetValue(p.AgentName, out var list))
                        unsessionedByName[p.AgentName] = list = new List<PaneInfo>();
                    list.Add(p);
                }
            }

            foreach (var e in entries ?? Array.Empty<AgentAttentionEntry>())
            {
                if (e == null) continue;

                PaneInfo pane = null;
                if (!string.IsNullOrEmpty(e.SessionId) && bySession.TryGetValue(e.SessionId, out var held))
                {
                    pane = held; // null when ambiguous: colour nothing
                    if (pane == null) continue;
                }
                else if (!string.IsNullOrEmpty(e.AgentName)
                         && unsessionedByName.TryGetValue(e.AgentName, out var named)
                         && named.Count == 1)
                {
                    pane = named[0];
                }

                if (pane == null) continue;

                var level = LevelFor(e.State, pane.IsHelper);
                if (level > result[pane.PaneId]) result[pane.PaneId] = level;
            }

            return result;
        }

        /// <summary>The wire value the header page understands.</summary>
        public static string WireName(PaneAttentionLevel level)
        {
            switch (level)
            {
                case PaneAttentionLevel.YourTurn: return "yourturn";
                case PaneAttentionLevel.Permission: return "permission";
                case PaneAttentionLevel.Question: return "question";
                default: return "none";
            }
        }

        /// <summary>
        /// The state's hue, the same as the Attention panel's cards (attention-panel.html: --yourturn,
        /// --peach, --mauve) so a pane and its card read as one signal.
        /// </summary>
        public static Color ColorFor(PaneAttentionLevel level, bool isDark)
        {
            switch (level)
            {
                case PaneAttentionLevel.YourTurn: return isDark ? ColorTranslator.FromHtml("#89dceb") : ColorTranslator.FromHtml("#04a5e5");
                case PaneAttentionLevel.Permission: return isDark ? ColorTranslator.FromHtml("#fab387") : ColorTranslator.FromHtml("#fe640b");
                case PaneAttentionLevel.Question: return isDark ? ColorTranslator.FromHtml("#cba6f7") : ColorTranslator.FromHtml("#8839ef");
                default: return Color.Transparent;
            }
        }

        private static readonly object IconLock = new object();
        private static readonly Dictionary<(PaneAttentionLevel, bool), Icon> Icons = new Dictionary<(PaneAttentionLevel, bool), Icon>();

        /// <summary>
        /// The tab icon for a level: a filled dot in the state's colour, or a fully transparent icon
        /// for None. Every document tab carries an icon once tab icons are on, so a pane without one
        /// keeps the same width and text position as one that has. Cached for the process: the set
        /// is small and fixed, and an icon handed to a tab must outlive it.
        /// </summary>
        public static Icon TabIcon(PaneAttentionLevel level, bool isDark)
        {
            if (level == PaneAttentionLevel.None) isDark = true; // blank either way: one icon

            lock (IconLock)
            {
                if (Icons.TryGetValue((level, isDark), out var cached)) return cached;

                using var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    if (level != PaneAttentionLevel.None)
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using var brush = new SolidBrush(ColorFor(level, isDark));
                        g.FillEllipse(brush, 3, 3, 10, 10);
                    }
                }

                // Icon.FromHandle does not own the handle, so it is never destroyed: at most eight
                // icons exist, each made once and kept for the process.
                var icon = Icon.FromHandle(bmp.GetHicon());
                Icons[(level, isDark)] = icon;
                return icon;
            }
        }
    }
}
