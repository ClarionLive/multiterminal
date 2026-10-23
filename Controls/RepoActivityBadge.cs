#nullable enable

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MultiTerminal.Models;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// What a terminal's status bar shows for its project's GitHub issues and pull requests (task
    /// df58b35d, GitHub #53): two counts, a "new" count, and a plain-text tooltip. Built from a
    /// <see cref="RepoActivitySummary"/> by <see cref="From"/>, serialized by <see cref="ToJson"/> and
    /// posted to statusbar.html as <c>repoActivity:{json}</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately carries no item, no URL and no token: the page only draws the badge. The list a click
    /// opens is native (<see cref="RepoActivityMenu"/>), so nothing the page could be tricked into
    /// opening ever reaches it. The JSON names below are read by statusbar.html's
    /// <c>setRepoActivity</c>; <c>RepoActivityUiTests</c> pins that every one of them is.
    /// </remarks>
    public sealed class RepoActivityBadge
    {
        private static readonly Regex UrlPattern = new(@"\b[A-Za-z][A-Za-z0-9+.-]*://\S+", RegexOptions.CultureInvariant);

        /// <summary>Open issues, or null when there is no list to count (never checked, no repository).</summary>
        [JsonPropertyName("issues")]
        public int? Issues { get; init; }

        /// <summary>Open pull requests, or null like <see cref="Issues"/>.</summary>
        [JsonPropertyName("prs")]
        public int? PullRequests { get; init; }

        /// <summary>Open items not seen yet.</summary>
        [JsonPropertyName("newCount")]
        public int NewCount { get; init; }

        /// <summary>A repository's list was a full page: the counts are "at least".</summary>
        [JsonPropertyName("truncated")]
        public bool Truncated { get; init; }

        /// <summary>The <see cref="RepoActivityState"/> name.</summary>
        [JsonPropertyName("state")]
        public string State { get; init; } = string.Empty;

        /// <summary>Plain text for the badge's title attribute; may hold a line break.</summary>
        [JsonPropertyName("tooltip")]
        public string Tooltip { get; init; } = string.Empty;

        /// <summary>True for every state but Ok and Checking: the counts are stale or there are none.</summary>
        [JsonPropertyName("dim")]
        public bool Dim { get; init; }

        /// <summary>
        /// The badge for <paramref name="summary"/>, or null (badge hidden) when there is no summary or the
        /// project is not watched. Pure: <paramref name="nowUtc"/> is only used for "last checked N ago".
        /// </summary>
        public static RepoActivityBadge? From(RepoActivitySummary? summary, DateTime? nowUtc = null)
        {
            if (summary == null || !summary.Watching) return null;

            DateTime now = nowUtc ?? DateTime.UtcNow;
            bool hasList = summary.State != RepoActivityState.NoRepos
                           && (summary.LastCheckedUtc.HasValue || summary.OpenIssues + summary.OpenPullRequests > 0);

            string counts = hasList ? CountsText(summary) : string.Empty;
            string tooltip;
            switch (summary.State)
            {
                case RepoActivityState.Ok:
                    tooltip = counts + LastChecked(summary.LastCheckedUtc, now);
                    break;
                case RepoActivityState.Checking:
                    tooltip = hasList ? counts + " — checking now" : "Checking now.";
                    break;
                default:
                    string reason = string.IsNullOrWhiteSpace(summary.Reason) ? DefaultReason(summary.State) : summary.Reason!;
                    tooltip = hasList ? counts + LastChecked(summary.LastCheckedUtc, now) + "\n" + reason : reason;
                    break;
            }

            return new RepoActivityBadge
            {
                Issues = hasList ? summary.OpenIssues : null,
                PullRequests = hasList ? summary.OpenPullRequests : null,
                NewCount = Math.Max(0, summary.NewCount),
                Truncated = summary.Truncated,
                State = summary.State.ToString(),
                Tooltip = StripUrls(tooltip),
                Dim = summary.State != RepoActivityState.Ok && summary.State != RepoActivityState.Checking,
            };
        }

        /// <summary>The <c>repoActivity:</c> payload: the badge as JSON, or <c>null</c> to hide it.</summary>
        public static string ToJson(RepoActivityBadge? badge)
            => badge == null ? "null" : JsonSerializer.Serialize(badge);

        /// <summary>"5 min ago", "3 h ago", "2 d ago"; "just now" under a minute (and for a clock skewed ahead).</summary>
        internal static string FormatAgo(DateTime thenUtc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc - thenUtc;
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1)) return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago";
            if (age < TimeSpan.FromDays(1)) return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago";
            return ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + " d ago";
        }

        private static string CountsText(RepoActivitySummary s)
        {
            string text = (s.Truncated ? "At least " : string.Empty)
                          + Plural(s.OpenIssues, "open issue", "open issues") + ", "
                          + Plural(s.OpenPullRequests, "open PR", "open PRs");
            if (s.NewCount > 0)
                text += $" ({s.NewCount.ToString(CultureInfo.InvariantCulture)} new since you last looked)";
            return text;
        }

        private static string LastChecked(DateTime? lastUtc, DateTime nowUtc)
            => lastUtc.HasValue ? " — last checked " + FormatAgo(lastUtc.Value, nowUtc) : string.Empty;

        private static string Plural(int n, string one, string many)
            => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

        private static string DefaultReason(RepoActivityState state) => state switch
        {
            RepoActivityState.NeverChecked => "Not checked yet.",
            RepoActivityState.NoRepos => "No GitHub repository found for this project.",
            RepoActivityState.NoAccess => "No access to the repository: check the project's source control account.",
            RepoActivityState.RateLimited => "Rate limited by GitHub.",
            _ => "The last check failed.",
        };

        // A reason is built by the service from GitHub's answer or an exception message. It never holds
        // the token (the client scrubs it), but a network exception can name an address; the badge does
        // not carry links of any kind, so drop anything that looks like one.
        private static string StripUrls(string text) => UrlPattern.Replace(text, "(link)");
    }
}
