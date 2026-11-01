using System;

namespace MultiTerminal.Services
{
    /// <summary>Why a project's last GitHub watch check failed, as far as the header needs to know.</summary>
    public enum GitHubCheckFailure
    {
        None,

        /// <summary>GitHub refused the token, or the linked account gives none. Fixed under Source Control Accounts.</summary>
        TokenRefused,

        /// <summary>No source control account is linked to the project. Fixed under Source Control Accounts.</summary>
        NoAccount,

        /// <summary>GitHub has no such repository that the account can see. Fixed in the project settings.</summary>
        RepoNotFound,

        /// <summary>Anything else (network, rate limit, a moved repo). The project settings show the details.</summary>
        Other,
    }

    /// <summary>One pane header's GitHub row (task e4924065; the failure, task 81d0e597).</summary>
    public sealed class GitHubBand
    {
        /// <summary>The band nobody sees: no project, or no github.com repository.</summary>
        public static readonly GitHubBand Hidden = new GitHubBand(null, null, null, GitHubCheckFailure.None);

        public GitHubBand(string repo, int? openIssues, int? openPullRequests, GitHubCheckFailure failure)
        {
            Repo = repo;
            OpenIssues = openIssues;
            OpenPullRequests = openPullRequests;
            Failure = failure;
        }

        /// <summary><c>owner/name</c>, or null to hide the row.</summary>
        public string Repo { get; }

        public int? OpenIssues { get; }

        public int? OpenPullRequests { get; }

        public GitHubCheckFailure Failure { get; }

        /// <summary>MT's own sentence for <see cref="Failure"/>, or null. Never the stored error text.</summary>
        public string FailureText => GitHubBandModel.TextOf(Failure);

        /// <summary>The fixed target the failure link sends ("accounts" or "project"), or null.</summary>
        public string FailureTarget => GitHubBandModel.TargetOf(Failure);

        /// <summary>The counts are from the last good check and a later one failed: shown dimmed.</summary>
        public bool CountsStale => Failure != GitHubCheckFailure.None && OpenIssues.HasValue && OpenPullRequests.HasValue;
    }

    /// <summary>
    /// What a pane header's GitHub row shows, from the project's watch settings and its stored watch
    /// state (task 81d0e597). Pure. The stored error is only CLASSIFIED, never shown: it can carry
    /// text from an exception message, and the header is MT's text only.
    /// </summary>
    public static class GitHubBandModel
    {
        /// <summary>The failure link's target for an account problem: opens Source Control Accounts.</summary>
        public const string AccountsTarget = "accounts";

        /// <summary>The failure link's target for any other problem: opens the project settings form.</summary>
        public const string ProjectTarget = "project";

        /// <summary>
        /// The row for a project's panes. <paramref name="repoFullName"/> null: no row. Watching off: the
        /// repo link only, as before. Watching on: the stored counts, plus the failure when the last
        /// attempt failed. A failure counts only while no success is as recent as the attempt, so the
        /// next good check clears it, and a card that could not be added after a good fetch (stored
        /// with the fetch's success time) does not show as a failed check.
        /// </summary>
        public static GitHubBand Build(string repoFullName, bool watching, ProjectDatabase.GitHubWatchStateRow state)
        {
            if (string.IsNullOrEmpty(repoFullName)) return GitHubBand.Hidden;
            if (!watching) return new GitHubBand(repoFullName, null, null, GitHubCheckFailure.None);
            if (state == null) return new GitHubBand(repoFullName, null, null, GitHubCheckFailure.None);

            bool lastAttemptFailed = !string.IsNullOrEmpty(state.LastError)
                && (state.LastSuccessUtc == null || (state.LastCheckedUtc.HasValue && state.LastSuccessUtc.Value < state.LastCheckedUtc.Value));
            return new GitHubBand(repoFullName, state.OpenIssues, state.OpenPullRequests,
                lastAttemptFailed ? Classify(state.LastError) : GitHubCheckFailure.None);
        }

        /// <summary>
        /// The class of a stored check error. Matched against the exact texts MT writes
        /// (<see cref="GitHubIssueFeed"/>, <see cref="GitHubWatchService"/>), so they cannot drift
        /// apart silently: anything unrecognised is <see cref="GitHubCheckFailure.Other"/>.
        /// </summary>
        public static GitHubCheckFailure Classify(string lastError)
        {
            if (string.IsNullOrEmpty(lastError)) return GitHubCheckFailure.None;
            if (lastError == GitHubIssueFeed.TokenRefusedError
                || lastError == GitHubIssueFeed.NoTokenError
                || lastError == GitHubWatchService.NoTokenError
                || lastError == GitHubWatchService.LegacyNoTokenError)
                return GitHubCheckFailure.TokenRefused;
            if (lastError == GitHubWatchService.NoAccountError) return GitHubCheckFailure.NoAccount;
            if (lastError.StartsWith(GitHubIssueFeed.RepoNotFoundErrorPrefix, StringComparison.Ordinal)) return GitHubCheckFailure.RepoNotFound;
            return GitHubCheckFailure.Other;
        }

        public static string TextOf(GitHubCheckFailure failure) => failure switch
        {
            GitHubCheckFailure.TokenRefused => "GitHub check failed: the account's token was refused",
            GitHubCheckFailure.NoAccount => "GitHub check failed: no GitHub account linked",
            GitHubCheckFailure.RepoNotFound => "GitHub check failed: repository not found or no access",
            GitHubCheckFailure.Other => "GitHub check failed. See the project's settings for details.",
            _ => null,
        };

        public static string TargetOf(GitHubCheckFailure failure) => failure switch
        {
            GitHubCheckFailure.TokenRefused or GitHubCheckFailure.NoAccount => AccountsTarget,
            GitHubCheckFailure.RepoNotFound or GitHubCheckFailure.Other => ProjectTarget,
            _ => null,
        };
    }
}
