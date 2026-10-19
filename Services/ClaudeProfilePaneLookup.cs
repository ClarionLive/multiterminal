using System;
using System.Collections.Generic;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Ticket c8ee0eec item 33: the seam from an AGENT NAME to the Claude profile of the terminal that
    /// agent is running in. The session services and REST controllers receive an agent name and a
    /// project path, and have no way to reach a terminal; without this they could only decide by
    /// folder, which answers Default for a folder no project owns even when the agent's pane runs
    /// under another profile.
    /// <para>MainForm calls <see cref="Configure"/> once, next to
    /// <see cref="ClaudeProfileProjectLookup.Configure"/>, with a function over its registered-terminal
    /// map. That function must be safe on any thread (callers are REST request threads and timers):
    /// MainForm's takes the map's own lock and reads only a reference that is replaced whole.</para>
    /// <para>Three answers: a <see cref="ClaudePaneProfile"/> (a live terminal is registered under that
    /// name), null (none is, or nothing is configured yet: the caller decides by folder), and
    /// <see cref="ClaudePaneProfile.Unknown"/> (the lookup threw, or more than one running terminal
    /// matches the name: the caller skips the data).</para>
    /// <para>Which terminal a name means is <see cref="Match"/>, not
    /// <c>TerminalDocument.FindByIdentity</c>: that one falls back to a tab's title even when the tab
    /// has a different confirmed name, and a title is whatever the user typed.</para>
    /// </summary>
    internal static class ClaudeProfilePaneLookup
    {
        private static volatile Func<string, ClaudePaneProfile> _forAgent;

        /// <summary>What <see cref="Match"/> needs to know about one terminal pane.</summary>
        /// <param name="ConfirmedName">The pane's identity for the launch in it
        /// (<c>TerminalDocument.OriginalAgentName</c>); null or empty when it has none yet.</param>
        /// <param name="CustomTitle">The tab title, which the user can change to anything.</param>
        /// <param name="IsRunning">Whether a terminal is running in the pane.</param>
        /// <param name="Profile">The pane's launch profile; null for a pane with no recorded launch.</param>
        internal readonly record struct PaneCandidate(string ConfirmedName, string CustomTitle, bool IsRunning, ClaudePaneProfile Profile);

        /// <summary>
        /// Pure: the Claude profile of the running pane <paramref name="agentName"/> refers to.
        /// <list type="bullet">
        /// <item>A pane that is not running never matches.</item>
        /// <item>A pane with a confirmed name matches by that name ONLY. Its title is not looked at, so
        /// renaming a tab to another agent's name does not make it that agent's pane.</item>
        /// <item>A pane with no confirmed name yet matches by its title.</item>
        /// <item>No match: null (no pane; the caller decides by folder).</item>
        /// <item>One match: its profile, or <see cref="ClaudePaneProfile.Unknown"/> when it has none.</item>
        /// <item>More than one match: <see cref="ClaudePaneProfile.Unknown"/>, never the first. Which
        /// of them the name means cannot be told, and a guess could read or write another profile's
        /// data.</item>
        /// </list>
        /// The comparison is the broker's for terminal names: OrdinalIgnoreCase, untrimmed.
        /// </summary>
        internal static ClaudePaneProfile Match(string agentName, IEnumerable<PaneCandidate> panes)
        {
            if (string.IsNullOrEmpty(agentName) || panes == null)
                return null;

            int matches = 0;
            ClaudePaneProfile found = null;
            foreach (var pane in panes)
            {
                if (!pane.IsRunning)
                    continue;

                string identity = string.IsNullOrEmpty(pane.ConfirmedName) ? pane.CustomTitle : pane.ConfirmedName;
                if (!string.Equals(identity, agentName, StringComparison.OrdinalIgnoreCase))
                    continue;

                matches++;
                found = pane.Profile ?? ClaudePaneProfile.Unknown;
            }

            return matches == 0 ? null : matches == 1 ? found : ClaudePaneProfile.Unknown;
        }

        /// <summary>Called once by MainForm. Null clears it (tests).</summary>
        internal static void Configure(Func<string, ClaudePaneProfile> forAgent) => _forAgent = forAgent;

        /// <summary>The profile of the live terminal registered as <paramref name="agentName"/>; null
        /// when there is none (or the name is blank, or MainForm has not configured the lookup).</summary>
        internal static ClaudePaneProfile ForAgent(string agentName) => ForAgent(agentName, _forAgent);

        /// <summary>Pure core of <see cref="ForAgent(string)"/>.</summary>
        internal static ClaudePaneProfile ForAgent(string agentName, Func<string, ClaudePaneProfile> forAgent)
        {
            if (string.IsNullOrWhiteSpace(agentName) || forAgent == null)
                return null;

            try
            {
                return forAgent(agentName);
            }
#pragma warning disable CA1031 // Whatever the lookup threw, the answer is "could not tell", which skips the data; it must not become "no pane", which would decide by folder.
            catch (Exception)
#pragma warning restore CA1031
            {
                return ClaudePaneProfile.Unknown;
            }
        }
    }
}
