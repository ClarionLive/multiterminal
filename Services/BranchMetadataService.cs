using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Per-(project, branch) outcome metadata. A branch's outcome is the user-facing
    /// capability the branch delivers — distinct from the commit subjects or task titles
    /// inside it. Persisted in the branch_metadata table.
    /// </summary>
    public class BranchMetadataService
    {
        private readonly SQLiteConnection _connection;
        private readonly MessageBroker _broker;
        // Pipeline Run 4 finding (Codex adversary HIGH): the service shares
        // TaskDatabase.Connection across three concurrent contexts (HUD background
        // refresh, UI/broker save, REST controllers). System.Data.SQLite's
        // connection is internally synchronized for command execution, but the
        // service-level lock here gives an explicit serialization boundary so
        // concurrent SetOutcome+GetOutcomes paths can't race on reader/writer
        // ordering. Scope: serializes ONLY branch_metadata operations — does
        // not synchronize with TaskDatabase's separate _dbLock (cross-service
        // serialization needs ticket dc33e59c-style refactoring; out of scope here).
        private readonly object _lock = new object();

        public BranchMetadataService(SQLiteConnection connection, MessageBroker broker = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _broker = broker;
        }

        public class BranchOutcome
        {
            public string BranchName { get; set; }
            public string Outcome { get; set; }
            public string DraftedBy { get; set; }
            public DateTime UpdatedAt { get; set; }
        }

        /// <summary>
        /// Idempotent upsert. Pass draftedBy='agent' or 'user' (free-form — caller decides).
        /// </summary>
        public void SetOutcome(string projectId, string branchName, string outcome, string draftedBy)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
            if (string.IsNullOrWhiteSpace(branchName)) throw new ArgumentException("branchName required", nameof(branchName));

            lock (_lock)
            {
                using var cmd = new SQLiteCommand(@"
                    INSERT INTO branch_metadata (project_id, branch_name, outcome, drafted_by, updated_at)
                    VALUES (@projectId, @branchName, @outcome, @draftedBy, @now)
                    ON CONFLICT(project_id, branch_name) DO UPDATE SET
                        outcome = @outcome,
                        drafted_by = @draftedBy,
                        updated_at = @now", _connection);

                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@branchName", branchName);
                cmd.Parameters.AddWithValue("@outcome", (object)outcome ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@draftedBy", (object)draftedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));

                cmd.ExecuteNonQuery();
            }

            // Fire the event OUTSIDE the lock — subscribers' handlers may do
            // arbitrary work (HudGitRenderer kicks RefreshAsync); holding the
            // lock during their re-entry could deadlock if they ever call back
            // into the service during the synchronous handler.
            _broker?.FireBranchOutcomeUpdated(projectId, branchName);
        }

        /// <summary>
        /// Returns all outcomes for the given project. Empty list if none.
        /// </summary>
        public List<BranchOutcome> GetOutcomes(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));

            var results = new List<BranchOutcome>();
            lock (_lock)
            {
                using var cmd = new SQLiteCommand(
                    "SELECT branch_name, outcome, drafted_by, updated_at " +
                    "FROM branch_metadata WHERE project_id = @projectId " +
                    "ORDER BY branch_name", _connection);
                cmd.Parameters.AddWithValue("@projectId", projectId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new BranchOutcome
                    {
                        BranchName = reader.GetString(0),
                        Outcome = reader.IsDBNull(1) ? null : reader.GetString(1),
                        DraftedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
                        UpdatedAt = reader.IsDBNull(3) ? DateTime.MinValue : DateTime.Parse(reader.GetString(3))
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// Returns a single outcome, or null if no row exists for the given (projectId, branchName).
        /// </summary>
        public BranchOutcome GetOutcome(string projectId, string branchName)
        {
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
            if (string.IsNullOrWhiteSpace(branchName)) throw new ArgumentException("branchName required", nameof(branchName));

            lock (_lock)
            {
                using var cmd = new SQLiteCommand(
                    "SELECT branch_name, outcome, drafted_by, updated_at " +
                    "FROM branch_metadata WHERE project_id = @projectId AND branch_name = @branchName", _connection);
                cmd.Parameters.AddWithValue("@projectId", projectId);
                cmd.Parameters.AddWithValue("@branchName", branchName);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                return new BranchOutcome
                {
                    BranchName = reader.GetString(0),
                    Outcome = reader.IsDBNull(1) ? null : reader.GetString(1),
                    DraftedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
                    UpdatedAt = reader.IsDBNull(3) ? DateTime.MinValue : DateTime.Parse(reader.GetString(3))
                };
            }
        }
    }
}
