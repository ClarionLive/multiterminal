using System;

namespace MultiTerminal.Docking
{
    /// <summary>
    /// A click on a terminal header's GitHub band (task e4924065): the pane's project and the page to
    /// open ("repo", "issues" or "pulls"). Carries no URL; MainForm builds it from the project's repo.
    /// </summary>
    public sealed class GitHubLinkRequestedEventArgs : EventArgs
    {
        public GitHubLinkRequestedEventArgs(string projectId, string target)
        {
            ProjectId = projectId;
            Target = target;
        }

        public string ProjectId { get; }

        public string Target { get; }
    }
}
