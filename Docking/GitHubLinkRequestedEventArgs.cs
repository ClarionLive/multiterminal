using System;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// A click on a terminal header's GitHub band (task e4924065): the pane's project and the page to
    /// open ("repo", "issues" or "pulls") and the repository the link was shown for (ticket a923589f).
    /// Carries no URL; MainForm checks the repository against the project's watch list and builds the URL.
    /// </summary>
    public sealed class GitHubLinkRequestedEventArgs : EventArgs
    {
        public GitHubLinkRequestedEventArgs(string projectId, string target, string repo = null)
        {
            ProjectId = projectId;
            Target = target;
            Repo = repo;
        }

        public string ProjectId { get; }

        public string Target { get; }

        /// <summary>The <c>owner/name</c> the page named: untrusted until it is found on the project's watch list.</summary>
        public string Repo { get; }
    }
}
