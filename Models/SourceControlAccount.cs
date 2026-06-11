using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents a single source control account (GitHub or Bitbucket) that can be
    /// assigned to projects for repo operations and commit attribution.
    /// Multiple accounts are supported; the auth token is never stored in SQLite —
    /// it lives in Windows Credential Manager under "MultiTerminal:SourceAccount:&lt;id&gt;".
    /// Stored as a row in the source_control_accounts table.
    /// </summary>
    public class SourceControlAccount
    {
        /// <summary>
        /// Stable 8-character identifier (Guid.NewGuid().ToString("N").Substring(0, 8)).
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Human-friendly label shown in the accounts manager UI.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Provider identifier — "github" or "bitbucket".
        /// </summary>
        public string Provider { get; set; } = "github";

        /// <summary>
        /// Account username for repo operations, URL construction, and Bitbucket basic auth.
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Whether an auth token is stored in Windows Credential Manager for this account.
        /// The actual token is never stored in SQLite.
        /// </summary>
        public bool HasToken { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
