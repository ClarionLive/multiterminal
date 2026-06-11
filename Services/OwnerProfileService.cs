using System;
using System.Data.SQLite;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Manages the owner profile (human user identity) stored in SQLite,
    /// with GitHub token secured via Windows Credential Manager (DPAPI).
    /// </summary>
    public class OwnerProfileService
    {
        private const string CredentialTargetName = "MultiTerminal:GitHubToken";

        private readonly SQLiteConnection _connection;

        public OwnerProfileService(SQLiteConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// Gets the owner profile, or null if not yet configured.
        /// </summary>
        public OwnerProfile GetProfile()
        {
            using var cmd = new SQLiteCommand(
                "SELECT id, full_name, email, github_username, has_github_token, created_at, updated_at " +
                "FROM owner_profile WHERE id = 'owner'", _connection);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new OwnerProfile
            {
                Id = reader.GetString(0),
                FullName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Email = reader.IsDBNull(2) ? null : reader.GetString(2),
                GitHubUsername = reader.IsDBNull(3) ? null : reader.GetString(3),
                HasGitHubToken = reader.GetInt32(4) != 0,
                CreatedAt = DateTime.Parse(reader.GetString(5)),
                UpdatedAt = DateTime.Parse(reader.GetString(6))
            };
        }

        /// <summary>
        /// Saves or updates the owner profile. Creates the row if it doesn't exist.
        /// </summary>
        public void SaveProfile(OwnerProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var now = DateTime.UtcNow.ToString("o");
            using var cmd = new SQLiteCommand(@"
                INSERT INTO owner_profile (id, full_name, email, github_username, has_github_token, created_at, updated_at)
                VALUES ('owner', @fullName, @email, @ghUser, @hasToken, @now, @now)
                ON CONFLICT(id) DO UPDATE SET
                    full_name = @fullName,
                    email = @email,
                    github_username = @ghUser,
                    has_github_token = @hasToken,
                    updated_at = @now", _connection);

            cmd.Parameters.AddWithValue("@fullName", (object)profile.FullName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@email", (object)profile.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ghUser", (object)profile.GitHubUsername ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hasToken", profile.HasGitHubToken ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", now);

            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns true if the owner profile has been configured (name and email set).
        /// </summary>
        public bool IsConfigured()
        {
            var profile = GetProfile();
            return profile != null
                && !string.IsNullOrWhiteSpace(profile.FullName)
                && !string.IsNullOrWhiteSpace(profile.Email);
        }

        /// <summary>
        /// Clears the legacy github_username field on the owner profile.
        /// Used by the one-time migration to the multi-account source control store;
        /// the GitHub identity now lives in source_control_accounts instead.
        /// </summary>
        public void ClearGitHubUsername()
        {
            using var cmd = new SQLiteCommand(
                "UPDATE owner_profile SET github_username = NULL, updated_at = @now WHERE id = 'owner'",
                _connection);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        #region GitHub Token — Windows Credential Manager

        /// <summary>
        /// Stores the GitHub token securely in Windows Credential Manager.
        /// Also updates the has_github_token flag in the database.
        /// </summary>
        public bool SaveGitHubToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            bool saved = WindowsCredentialStore.Write(CredentialTargetName, token);
            if (saved)
            {
                using var cmd = new SQLiteCommand(
                    "UPDATE owner_profile SET has_github_token = 1, updated_at = @now WHERE id = 'owner'",
                    _connection);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            return saved;
        }

        /// <summary>
        /// Reads the GitHub token from Windows Credential Manager.
        /// Returns null if not found.
        /// </summary>
        public string GetGitHubToken()
        {
            return WindowsCredentialStore.Read(CredentialTargetName);
        }

        /// <summary>
        /// Removes the GitHub token from Windows Credential Manager
        /// and clears the has_github_token flag.
        /// </summary>
        public bool RemoveGitHubToken()
        {
            bool deleted = WindowsCredentialStore.Delete(CredentialTargetName);
            using var cmd = new SQLiteCommand(
                "UPDATE owner_profile SET has_github_token = 0, updated_at = @now WHERE id = 'owner'",
                _connection);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            return deleted;
        }

        #endregion
    }
}
