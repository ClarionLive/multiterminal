using System;
using System.Data.SQLite;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
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

        #region GitHub Token — Windows Credential Manager

        /// <summary>
        /// Stores the GitHub token securely in Windows Credential Manager.
        /// Also updates the has_github_token flag in the database.
        /// </summary>
        public bool SaveGitHubToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            bool saved = CredentialManagerWrite(CredentialTargetName, token);
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
            return CredentialManagerRead(CredentialTargetName);
        }

        /// <summary>
        /// Removes the GitHub token from Windows Credential Manager
        /// and clears the has_github_token flag.
        /// </summary>
        public bool RemoveGitHubToken()
        {
            bool deleted = CredentialManagerDelete(CredentialTargetName);
            using var cmd = new SQLiteCommand(
                "UPDATE owner_profile SET has_github_token = 0, updated_at = @now WHERE id = 'owner'",
                _connection);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
            return deleted;
        }

        #endregion

        #region Windows Credential Manager P/Invoke

        private static bool CredentialManagerWrite(string target, string secret)
        {
            var byteArray = Encoding.Unicode.GetBytes(secret);

            var credential = new NativeCredential
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)byteArray.Length,
                CredentialBlob = Marshal.AllocHGlobal(byteArray.Length),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = target
            };

            try
            {
                Marshal.Copy(byteArray, 0, credential.CredentialBlob, byteArray.Length);
                return CredWrite(ref credential, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(credential.CredentialBlob);
            }
        }

        private static string CredentialManagerRead(string target)
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
                return null;

            try
            {
                var cred = Marshal.PtrToStructure<NativeCredential>(credPtr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                    return null;

                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            finally
            {
                CredFree(credPtr);
            }
        }

        private static bool CredentialManagerDelete(string target)
        {
            return CredDelete(target, CRED_TYPE_GENERIC, 0);
        }

        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string target, int type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDelete(string target, int type, uint flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr credential);

        #endregion
    }
}
