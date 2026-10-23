using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// What MT last fetched from one GitHub repo for one project, and how far the user has seen
    /// (task df58b35d, GitHub #53). One <c>repo_watch_state</c> row per (project, repo).
    /// Issue and PR numbers share one increasing sequence per repo, so "seen" is a single high-water
    /// mark: an open item is new when its number is above <see cref="SeenMaxNumber"/>.
    /// </summary>
    public sealed class RepoWatchState
    {
        public string ProjectId { get; set; }

        /// <summary><c>owner/repo</c>, lower-cased by <c>TaskDatabase</c> on every read and write.</summary>
        public string Repo { get; set; }

        /// <summary>The ETag of the last 200 response, sent back as <c>If-None-Match</c>; null before one.</summary>
        public string ETag { get; set; }

        /// <summary>When the last check finished (UTC), whatever its outcome; null when never checked.</summary>
        public DateTime? LastCheckedAt { get; set; }

        /// <summary>The last check's outcome, one of <see cref="RepoWatchStatus"/>. Opaque to the storage layer.</summary>
        public string LastStatus { get; set; }

        /// <summary>Detail for <see cref="LastStatus"/> (an error text, say), or null.</summary>
        public string LastMessage { get; set; }

        /// <summary>
        /// The highest issue/PR number the user has seen. Null = never initialized: the first successful
        /// check sets it to the current maximum, so a newly watched repo does not report its whole history
        /// as new. Only ever raised, never lowered.
        /// </summary>
        public long? SeenMaxNumber { get; set; }

        /// <summary>The last fetched list, serialized by the polling service. Opaque to the storage layer.</summary>
        public string SnapshotJson { get; set; }
    }

    /// <summary>
    /// The values <c>repo_watch_state.last_status</c> holds. Stored text: renaming one orphans every
    /// existing row with the old value (it would read as <see cref="Error"/>).
    /// </summary>
    public static class RepoWatchStatus
    {
        /// <summary>The last check got a list (200) or confirmed it unchanged (304).</summary>
        public const string Ok = "ok";

        /// <summary>GitHub's rate limit was hit; the service backs off until its reset time.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary>401 or 404: a bad token, no such repository, or a private one this token cannot see.</summary>
        public const string NoAccess = "no_access";

        /// <summary>Anything else that failed.</summary>
        public const string Error = "error";
    }
}
