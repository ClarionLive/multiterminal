using MultiTerminal.Docking;

namespace MultiTerminal.Services
{
    /// <summary>
    /// What a "Just Claude" launch registers as, given the Launch Claude picker's choice (ticket ae3d0193).
    /// Pure, so the rule is testable without WinForms; <c>MainForm.OnStartScreenJustClaude</c> asks here.
    ///
    /// <para><b>"Unassigned" takes no team identity.</b> It is the shared placeholder, registered exactly
    /// the way Open PowerShell registers a project-less pane (<c>Broker.RegisterTerminal("Unassigned",
    /// docId, nonce)</c>): not as a team lead, and never through <c>PreRegisterTerminalWithName</c>, whose
    /// refusal fallback picks the first free POOL name (Grace, Charlie…), which is exactly the team-name
    /// grab this choice exists to avoid. The broker gives each placeholder pane its own row, the MCP server
    /// and the plugin post no credentials under it, and the session can still claim a real name later with
    /// <c>register_terminal</c> (the broker's placeholder-to-name rename).</para>
    ///
    /// <para>Not the no-team-lead PROJECT rule: since 6a8d029f a project with no lead launches under a
    /// name derived from the project, and Just Claude has no project to derive one from. The placeholder
    /// is the existing project-less "no identity" path.</para>
    ///
    /// <para><b>A named agent</b> launches exactly as before this ticket: under that name, as team lead.</para>
    /// </summary>
    /// <param name="TerminalName">The name to register and start the terminal under.</param>
    /// <param name="IsTeamLead">Passed to registration and to <c>StartTerminal</c>.</param>
    /// <param name="IsPlaceholder">Register as the shared placeholder (Open PowerShell's path) and skip the
    /// active-task spawn-directory lookup, since a placeholder has no tasks.</param>
    internal readonly record struct JustClaudeLaunchIdentity(string TerminalName, bool IsTeamLead, bool IsPlaceholder)
    {
        /// <summary>Maps the picker's selected entry to the launch identity. Null or blank means the placeholder.</summary>
        internal static JustClaudeLaunchIdentity FromPickerSelection(string selected)
        {
            if (string.IsNullOrWhiteSpace(selected) || TerminalRegistrationBinder.IsUnassigned(selected.Trim()))
                return new JustClaudeLaunchIdentity(TerminalRegistrationBinder.UnassignedPlaceholder, IsTeamLead: false, IsPlaceholder: true);

            return new JustClaudeLaunchIdentity(selected, IsTeamLead: true, IsPlaceholder: false);
        }
    }
}
