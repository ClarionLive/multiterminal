using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Manages multiple source control accounts (GitHub / Bitbucket) stored in SQLite,
    /// with auth tokens secured via Windows Credential Manager (DPAPI) rather than the database.
    /// Tokens are namespaced under "MultiTerminal:SourceAccount:&lt;id&gt;".
    /// </summary>
    public class SourceControlAccountService
    {
        private const string CredentialTargetPrefix = "MultiTerminal:SourceAccount:";

        private readonly SQLiteConnection _connection;

        public SourceControlAccountService(SQLiteConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// Returns all source control accounts, ordered by display name.
        /// </summary>
        public List<SourceControlAccount> GetAll()
        {
            var accounts = new List<SourceControlAccount>();

            using var cmd = new SQLiteCommand(
                "SELECT id, display_name, provider, username, has_token, created_at, updated_at " +
                "FROM source_control_accounts ORDER BY display_name COLLATE NOCASE", _connection);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                accounts.Add(ReadAccount(reader));
            }

            return accounts;
        }

        /// <summary>
        /// Gets a single account by id, or null if not found.
        /// </summary>
        public SourceControlAccount Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            using var cmd = new SQLiteCommand(
                "SELECT id, display_name, provider, username, has_token, created_at, updated_at " +
                "FROM source_control_accounts WHERE id = @id", _connection);
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return ReadAccount(reader);
        }

        /// <summary>
        /// Inserts a new account. Generates an 8-character id if the account doesn't already have one.
        /// Returns the stored account (with id and timestamps populated).
        /// </summary>
        public SourceControlAccount Add(SourceControlAccount account)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));

            if (string.IsNullOrEmpty(account.Id))
            {
                account.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            var now = DateTime.UtcNow;
            account.CreatedAt = now;
            account.UpdatedAt = now;
            var nowStr = now.ToString("o");

            using var cmd = new SQLiteCommand(@"
                INSERT INTO source_control_accounts (id, display_name, provider, username, has_token, created_at, updated_at)
                VALUES (@id, @displayName, @provider, @username, @hasToken, @now, @now)", _connection);

            cmd.Parameters.AddWithValue("@id", account.Id);
            cmd.Parameters.AddWithValue("@displayName", (object)account.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@provider", string.IsNullOrEmpty(account.Provider) ? "github" : account.Provider);
            cmd.Parameters.AddWithValue("@username", (object)account.Username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hasToken", account.HasToken ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", nowStr);

            cmd.ExecuteNonQuery();

            return account;
        }

        /// <summary>
        /// Updates display name, provider, and username of an existing account.
        /// The has_token flag is managed by the token operations, not here.
        /// </summary>
        public void Update(SourceControlAccount account)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (string.IsNullOrEmpty(account.Id)) throw new ArgumentException("Account id is required.", nameof(account));

            var now = DateTime.UtcNow;
            account.UpdatedAt = now;

            using var cmd = new SQLiteCommand(@"
                UPDATE source_control_accounts SET
                    display_name = @displayName,
                    provider = @provider,
                    username = @username,
                    updated_at = @now
                WHERE id = @id", _connection);

            cmd.Parameters.AddWithValue("@id", account.Id);
            cmd.Parameters.AddWithValue("@displayName", (object)account.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@provider", string.IsNullOrEmpty(account.Provider) ? "github" : account.Provider);
            cmd.Parameters.AddWithValue("@username", (object)account.Username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", now.ToString("o"));

            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes an account and removes its token from Windows Credential Manager.
        /// </summary>
        public void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            // Best-effort cleanup of the stored token; the DB row is the source of truth.
            WindowsCredentialStore.Delete(CredentialTargetPrefix + id);

            using var cmd = new SQLiteCommand(
                "DELETE FROM source_control_accounts WHERE id = @id", _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        #region Token — Windows Credential Manager

        /// <summary>
        /// Stores the auth token for an account in Windows Credential Manager
        /// and sets the has_token flag. Returns true on success.
        /// </summary>
        public bool SaveToken(string id, string token)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (string.IsNullOrEmpty(token)) return false;

            bool saved = WindowsCredentialStore.Write(CredentialTargetPrefix + id, token);
            if (saved)
            {
                SetHasToken(id, true);
            }
            return saved;
        }

        /// <summary>
        /// Reads the auth token for an account from Windows Credential Manager.
        /// Returns null if not found.
        /// </summary>
        public string GetToken(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return WindowsCredentialStore.Read(CredentialTargetPrefix + id);
        }

        /// <summary>
        /// Removes the auth token for an account from Windows Credential Manager
        /// and clears the has_token flag. Returns true if a credential was deleted.
        /// </summary>
        public bool RemoveToken(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            bool deleted = WindowsCredentialStore.Delete(CredentialTargetPrefix + id);
            SetHasToken(id, false);
            return deleted;
        }

        private void SetHasToken(string id, bool hasToken)
        {
            using var cmd = new SQLiteCommand(
                "UPDATE source_control_accounts SET has_token = @hasToken, updated_at = @now WHERE id = @id",
                _connection);
            cmd.Parameters.AddWithValue("@hasToken", hasToken ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        #endregion

        private static SourceControlAccount ReadAccount(SQLiteDataReader reader)
        {
            return new SourceControlAccount
            {
                Id = reader.GetString(0),
                DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Provider = reader.IsDBNull(2) ? "github" : reader.GetString(2),
                Username = reader.IsDBNull(3) ? null : reader.GetString(3),
                HasToken = reader.GetInt32(4) != 0,
                CreatedAt = DateTime.Parse(reader.GetString(5)),
                UpdatedAt = DateTime.Parse(reader.GetString(6))
            };
        }
    }
}
