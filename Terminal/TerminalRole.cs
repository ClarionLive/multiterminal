namespace MultiTerminal.Terminal
{
    /// <summary>
    /// What a terminal is to its project, decided once at launch from the two values the launch
    /// already carries (task ad7f6721).
    /// </summary>
    internal enum TerminalRole
    {
        /// <summary>No project, or nothing that makes this terminal a PM or a helper — Just Claude,
        /// Oracle, "Launch as…". These tabs show no role at all.</summary>
        None = 0,

        /// <summary>The Owner opened this terminal on a project: it is that project's Project Manager
        /// (task 760827ad).</summary>
        ProjectManager,

        /// <summary>Started by <c>spawn_helper</c> on behalf of another agent.</summary>
        Helper,
    }

    /// <summary>
    /// THE one place that decides a terminal's role. Both readers go through here:
    /// <see cref="ConPtyTerminal.BuildProjectPmEnvAssignment"/>, which sets the
    /// <c>MULTITERMINAL_PROJECT_PM</c> launch variable the session-start hook reads, and
    /// <c>TerminalDocument.UpdateTabTitle</c>, which labels the tab.
    ///
    /// <para>Kept as one resolver on purpose. A second copy of "project and no spawner" in the UI
    /// would drift from the launch variable, and the visible symptom is the worst kind: a tab that
    /// says PM while the agent inside was never given the role (or the reverse). The agreement is
    /// asserted in <c>TerminalRoleTests</c> rather than promised in a comment.</para>
    /// </summary>
    internal static class TerminalRoles
    {
        /// <summary>Suffix appended to a tab title, e.g. <c>Alice - MultiTerminal (PM)</c>. Empty for
        /// <see cref="TerminalRole.None"/>, which is what keeps non-project tabs exactly as they were.</summary>
        internal static string TabSuffix(TerminalRole role)
        {
            switch (role)
            {
                case TerminalRole.ProjectManager: return " (PM)";
                case TerminalRole.Helper: return " (Helper)";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// A spawned helper is a helper whether or not it has a project — the spawner is exactly what
        /// makes the hook withhold the PM role, so that test comes first. A terminal with a project and
        /// no spawner is the project's PM. Everything else has no role.
        ///
        /// <para>Uses <see cref="string.IsNullOrEmpty(string)"/> for both, matching the
        /// <c>MULTITERMINAL_PROJECT_ID</c> and <c>MULTITERMINAL_SPAWNER</c> branches in
        /// <see cref="ConPtyTerminal.Start"/>: a whitespace-only value must not be a project for one
        /// reader and not for the other.</para>
        /// </summary>
        internal static TerminalRole Resolve(string projectId, string spawnerName)
        {
            if (!string.IsNullOrEmpty(spawnerName))
                return TerminalRole.Helper;

            return !string.IsNullOrEmpty(projectId)
                ? TerminalRole.ProjectManager
                : TerminalRole.None;
        }

        /// <summary>
        /// True when this launch gets <c>MULTITERMINAL_PROJECT_PM='true'</c>. The env variable and the
        /// tab label are then two renderings of one decision rather than two rules.
        /// </summary>
        internal static bool IsProjectManager(string projectId, string spawnerName) =>
            Resolve(projectId, spawnerName) == TerminalRole.ProjectManager;
    }
}
