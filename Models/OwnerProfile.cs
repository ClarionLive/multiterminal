using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents the human owner/user of MultiTerminal.
    /// Distinct from TeamMemberProfile which represents AI agent identities.
    /// Stored as a single row in the owner_profile table.
    /// </summary>
    public class OwnerProfile
    {
        /// <summary>
        /// Singleton row ID — always "owner".
        /// </summary>
        public string Id { get; set; } = "owner";

        /// <summary>
        /// Full name used for git user.name and commit attribution.
        /// </summary>
        public string FullName { get; set; }

        /// <summary>
        /// Email used for git user.email.
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// GitHub username for repo operations and URL construction.
        /// </summary>
        public string GitHubUsername { get; set; }

        /// <summary>
        /// Whether a GitHub token is stored in Windows Credential Manager.
        /// The actual token is never stored in SQLite.
        /// </summary>
        public bool HasGitHubToken { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
