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

        /// <summary>
        /// Whether this launch is a quiet start (GitHub #34, task e0fa9d90): the project has Quiet start
        /// on AND the terminal is not a spawned helper. A helper is never quiet: it must get its first
        /// turn to collect its job (get_my_spawn_job), and nobody is waiting to type into it.
        ///
        /// <para>The one decision behind <c>MULTITERMINAL_QUIET_START</c> (the SessionStart hook's
        /// signal), MT's "initializing..." kick, and the post-/clear kick. If those disagreed, a
        /// terminal would get the menu despite the setting, or sit idle with the hook still asking
        /// for session-start.</para>
        /// </summary>
        internal static bool IsQuietStart(bool projectQuietStart, string spawnerName) =>
            projectQuietStart && Resolve(null, spawnerName) != TerminalRole.Helper;

        /// <summary>
        /// The role a tab may actually SHOW, given the project it is currently naming.
        ///
        /// <para><see cref="Resolve"/> answers "what was this terminal granted at launch", and that answer
        /// never changes — <c>MULTITERMINAL_PROJECT_PM</c> is fixed in the child process env at launch and
        /// nothing rewrites it. But the project NAME beside the role does change: the statusline poll
        /// re-resolves it from the agent's current folder. Composing a launch-scoped role with a
        /// current-workspace project name is how a tab comes to read <c>"Alice - OtherProject (PM)"</c> for
        /// authority granted somewhere else entirely — the very "tab claims a role the hook withheld"
        /// failure this class exists to prevent, reached from the other side (task ad7f6721, pipeline Run 1;
        /// found independently by the debugger and the cross-model adversary gates).</para>
        ///
        /// <para>Only <see cref="TerminalRole.ProjectManager"/> is project-scoped, because only it is a claim
        /// ABOUT the named project. <see cref="TerminalRole.Helper"/> is a fact about who spawned this
        /// terminal, which stays true wherever the agent walks, so it is returned unchanged.</para>
        ///
        /// <para><paramref name="currentProjectKnown"/> is the distinction that makes this correct, and an
        /// earlier version of this method got it wrong by collapsing two different situations into one null:</para>
        /// <list type="bullet">
        /// <item>The lookup could not run — broker not ready, registry empty, the resolver threw. We do not
        /// know where the agent is, so we change nothing and KEEP the badge. Guessing "departed" here would
        /// stick: the poll only re-runs when the folder CHANGES, so a badge dropped on a transient failure
        /// stays dropped until the agent happens to move again.</item>
        /// <item>The lookup ran fine and matched nothing — the agent is positively outside every registered
        /// project. That IS a departure from the launch project, so the badge goes. Treating this as "unknown"
        /// is what produced <c>"Alice - Scratch (PM)"</c>: the project NAME beside the role is downgraded to
        /// the folder's leaf name whether or not a project resolved, so keeping the role there re-created the
        /// exact two-values-moving-at-different-rates fault this method exists to remove (pipeline Run 2,
        /// debugger + cross-model adversary, again independently).</item>
        /// </list>
        ///
        /// <para>Derived, not destructive: the caller's launch role is never overwritten, so walking back into
        /// the launch project restores the badge.</para>
        /// </summary>
        internal static TerminalRole ForDisplay(
            TerminalRole launchRole, string launchProjectId, string currentProjectId, bool currentProjectKnown)
        {
            if (launchRole != TerminalRole.ProjectManager)
                return launchRole;

            // Could not tell where the agent is — leave the label as it was.
            if (!currentProjectKnown)
                return launchRole;

            // Resolved, and the agent is in no registered project at all.
            if (string.IsNullOrEmpty(currentProjectId))
                return TerminalRole.None;

            return string.Equals(currentProjectId, launchProjectId, System.StringComparison.Ordinal)
                ? launchRole
                : TerminalRole.None;
        }
    }
}
