using System;
using System.Collections.Generic;
using System.Linq;
using MultiTerminal.Terminal;

namespace MultiTerminal.Services
{
    /// <summary>What <see cref="ProjectAlreadyOpen"/> needs to know about one open pane.</summary>
    /// <param name="DocId">The pane's document id.</param>
    /// <param name="LaunchProjectId">The project the pane's current launch was for; null when it was not a project launch.</param>
    /// <param name="Role">The role that launch was given (<see cref="TerminalRoles.Resolve"/>).</param>
    /// <param name="IsRunning">Whether the pane's process is running.</param>
    /// <param name="OnStartScreen">Whether the pane is showing its start screen.</param>
    /// <param name="Title">The pane's tab text, for the question.</param>
    internal sealed record OpenPaneFacts(string DocId, string LaunchProjectId, TerminalRole Role, bool IsRunning, bool OnStartScreen, string Title);

    /// <summary>
    /// GitHub #95: launching a project that is already open in another pane used to open a second one
    /// with no word, giving the project a second PM (with a suffixed name) that the Owner might not
    /// know about. The launch now asks first; this is the pure rule for when.
    /// </summary>
    internal static class ProjectAlreadyOpen
    {
        /// <summary>
        /// The panes that already have <paramref name="projectId"/> open, in the order given.
        /// A pane counts only when its current launch was the Owner opening that project (its role is
        /// <see cref="TerminalRole.ProjectManager"/>: a helper a PM spawned is the PM's business, not a
        /// second copy of the project), its process is running, it is not back on its start screen, and
        /// it is not the pane being launched. Project ids are compared exactly, as the database stores them.
        /// </summary>
        internal static IReadOnlyList<OpenPaneFacts> FindOpenPanes(string projectId, string launchingDocId, IEnumerable<OpenPaneFacts> panes)
        {
            if (string.IsNullOrEmpty(projectId) || panes == null)
                return Array.Empty<OpenPaneFacts>();

            return panes
                .Where(p => p != null
                    && p.Role == TerminalRole.ProjectManager
                    && p.IsRunning
                    && !p.OnStartScreen
                    && string.Equals(p.LaunchProjectId, projectId, StringComparison.Ordinal)
                    && !string.Equals(p.DocId, launchingDocId, StringComparison.Ordinal))
                .ToList();
        }

        /// <summary>
        /// The question's body: which pane holds the project and where it is, and what the choices do.
        /// <paramref name="floating"/> says whether that pane is in a floating window.
        /// </summary>
        internal static string Describe(IReadOnlyList<OpenPaneFacts> open, bool floating)
        {
            if (open == null || open.Count == 0) return string.Empty;
            string where = floating ? " (in a floating window)" : string.Empty;
            string text = $"It is running in \"{open[0].Title}\"{where}.";
            if (open.Count > 1)
                text += $" {open.Count - 1} more pane{(open.Count == 2 ? " has" : "s have")} it open too.";
            return text + "\n\nOpening another pane gives the project a second Project Manager on the same board.";
        }
    }
}
