using System;

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
    /// <see cref="ClaudePaneProfile.Unknown"/> (the lookup threw: the caller skips the data).</para>
    /// </summary>
    internal static class ClaudeProfilePaneLookup
    {
        private static volatile Func<string, ClaudePaneProfile> _forAgent;

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
