#nullable enable

using System;
using System.Collections.Generic;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Where a watched repository, or a project's repositories as a whole, stand
    /// (task df58b35d, GitHub #53). Produced by <see cref="Services.RepoActivityService.GetSummary"/>.
    /// </summary>
    public enum RepoActivityState
    {
        /// <summary>The last check succeeded (200 or 304).</summary>
        Ok = 0,

        /// <summary>A check is running now.</summary>
        Checking,

        /// <summary>Watched, but not checked yet.</summary>
        NeverChecked,

        /// <summary>No GitHub repository is listed in the settings, among the folder's remotes, or in the project's Git URL.</summary>
        NoRepos,

        /// <summary>GitHub answered 401 (bad token) or 404 (no such repository, or a private one this token cannot see).</summary>
        NoAccess,

        /// <summary>GitHub's rate limit; the repository is skipped until the time GitHub gave.</summary>
        RateLimited,

        /// <summary>Any other failure (network, timeout, an unexpected answer).</summary>
        Error,

        /// <summary>Watching is off for this project, or for the whole app (<c>MULTITERMINAL_REPO_WATCH</c>).</summary>
        Disabled,
    }

    /// <summary>
    /// The one default wording for each <see cref="RepoActivityState"/>, used when nothing more specific
    /// is known (GitHub's own message, when there is one, is shown instead). The service and the badge
    /// both read it, so the tooltip and the list can never describe the same state two ways.
    /// </summary>
    public static class RepoActivityReasons
    {
        public static string For(RepoActivityState state) => state switch
        {
            RepoActivityState.Ok => string.Empty,
            RepoActivityState.Checking => "Checking now.",
            RepoActivityState.NeverChecked => "Not checked yet.",
            RepoActivityState.NoRepos =>
                "No GitHub repository to watch: list one in the project settings, or give the project folder a github.com remote.",
            RepoActivityState.NoAccess => "GitHub refused access to this repository.",
            RepoActivityState.RateLimited => "GitHub's rate limit was reached.",
            RepoActivityState.Disabled => "Watching issues and pull requests is off for this project.",
            _ => "The last check failed.",
        };
    }

    /// <summary>One open issue or pull request as the status bar shows it.</summary>
    public sealed class RepoActivityEntry
    {
        public RepoActivityEntry(RepoActivityItem item, bool isNew, string url)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
            IsNew = isNew;
            Url = url ?? throw new ArgumentNullException(nameof(url));
        }

        /// <summary>The item as last fetched (text fields already display-safe).</summary>
        public RepoActivityItem Item { get; }

        /// <summary>True when its number is above the repository's seen mark.</summary>
        public bool IsNew { get; }

        /// <summary>
        /// <see cref="RepoActivityItem.HtmlUrl"/>, or when that is null the item's page built from the
        /// watched repository's name (<c>https://github.com/{repo}/issues/{n}</c> or <c>/pull/{n}</c>;
        /// GitHub redirects a renamed repository).
        /// </summary>
        public string Url { get; }
    }

    /// <summary>One watched repository of a project.</summary>
    public sealed class RepoActivityRepo
    {
        /// <summary><c>owner/repo</c>.</summary>
        public string FullName { get; init; } = string.Empty;

        public RepoActivityState State { get; init; }

        /// <summary>Human text for a tooltip (never the token); null when there is nothing to say.</summary>
        public string? Reason { get; init; }

        /// <summary>When the last check of this repository finished, whatever its outcome.</summary>
        public DateTime? LastCheckedUtc { get; init; }

        /// <summary>True when the last list was a full page: there may be more open items than listed.</summary>
        public bool Truncated { get; init; }

        /// <summary>The open items, new first, then newest number first.</summary>
        public IReadOnlyList<RepoActivityEntry> Items { get; init; } = Array.Empty<RepoActivityEntry>();
    }

    /// <summary>A project's repository activity, for its status bar.</summary>
    public sealed class RepoActivitySummary
    {
        public string ProjectId { get; init; } = string.Empty;

        /// <summary>True when the project has watching on and the app has not turned it off.</summary>
        public bool Watching { get; init; }

        /// <summary>
        /// The worst state across <see cref="Repos"/>, except that the project is Ok when at least one
        /// repository is Ok and the others are only Checking or NeverChecked.
        /// </summary>
        public RepoActivityState State { get; init; }

        /// <summary>Human text for a tooltip (never the token); null when there is nothing to say.</summary>
        public string? Reason { get; init; }

        public int OpenIssues { get; init; }

        public int OpenPullRequests { get; init; }

        /// <summary>Open items above their repository's seen mark.</summary>
        public int NewCount { get; init; }

        /// <summary>True when any repository's last list was a full page.</summary>
        public bool Truncated { get; init; }

        /// <summary>The most recent check of any repository.</summary>
        public DateTime? LastCheckedUtc { get; init; }

        public IReadOnlyList<RepoActivityRepo> Repos { get; init; } = Array.Empty<RepoActivityRepo>();
    }

    /// <summary>Raised by <see cref="Services.RepoActivityService.RepoActivityChanged"/>.</summary>
    public sealed class RepoActivityChangedEventArgs : EventArgs
    {
        public RepoActivityChangedEventArgs(string projectId)
        {
            ProjectId = projectId;
        }

        public string ProjectId { get; }
    }
}
