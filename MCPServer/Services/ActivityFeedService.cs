using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MultiTerminal.Services;

namespace MultiTerminal.MCPServer.Services
{
    /// <summary>
    /// Service for recording high-level activity events to a dedicated feed.
    /// Shows manager-level view: plan lifecycle, phase transitions, builds.
    /// NOT for granular updates or chat messages.
    /// </summary>
    public class ActivityFeedService : IDisposable
    {
        private readonly string _databasePath;
        private SQLiteConnection _connection;
        private bool _isDisposed;

        // Valid activity types (whitelist approach)
        public static readonly HashSet<string> ValidPlanTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PLAN_CREATED", "PLAN_ACTIVATED", "PLAN_SUBMITTED", "PLAN_APPROVED", "PLAN_REJECTED"
        };

        public static readonly HashSet<string> ValidPhaseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PHASE_STARTED", "PHASE_COMPLETED"
        };

        public static readonly HashSet<string> ValidBuildTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BUILD_STARTED", "BUILD_SUCCEEDED", "BUILD_FAILED"
        };

        public static readonly HashSet<string> ValidToolTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TOOL_START", "TOOL_COMPLETE", "TOOL_FAILED"
        };

        public static readonly HashSet<string> ValidSubagentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SUBAGENT_START", "SUBAGENT_COMPLETE", "SUBAGENT_FAILED"
        };

        /// <summary>
        /// Raised when a new activity is recorded, for UI refresh.
        /// </summary>
        public event EventHandler<ActivityFeedEntry> ActivityRecorded;

        public ActivityFeedService()
        {
            _databasePath = TaskDatabase.GetDatabasePath();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            var connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Version = 3,
                JournalMode = SQLiteJournalModeEnum.Wal,
                Pooling = true
            }.ToString();

            _connection = new SQLiteConnection(connectionString);
            _connection.Open();

            CreateSchema();
        }

        private void CreateSchema()
        {
            const string schema = @"
                CREATE TABLE IF NOT EXISTS activity_feed (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp TEXT NOT NULL,
                    activity_type TEXT NOT NULL,
                    plan_id TEXT,
                    phase_id TEXT,
                    actor TEXT,
                    summary TEXT NOT NULL,
                    severity TEXT DEFAULT 'info',
                    details_json TEXT,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS idx_activity_plan_time ON activity_feed(plan_id, timestamp);
                CREATE INDEX IF NOT EXISTS idx_activity_type_time ON activity_feed(activity_type, timestamp);
                CREATE INDEX IF NOT EXISTS idx_activity_timestamp ON activity_feed(timestamp DESC);
            ";

            using var command = new SQLiteCommand(schema, _connection);
            command.ExecuteNonQuery();

            // Migration: add project_id column if missing
            try
            {
                using var migrateCmd = new SQLiteCommand(
                    "ALTER TABLE activity_feed ADD COLUMN project_id TEXT", _connection);
                migrateCmd.ExecuteNonQuery();

                using var indexCmd = new SQLiteCommand(
                    "CREATE INDEX IF NOT EXISTS idx_activity_project_time ON activity_feed(project_id, timestamp DESC)",
                    _connection);
                indexCmd.ExecuteNonQuery();
            }
            catch (SQLiteException) { /* column already exists */ }
        }

        #region Record Methods

        /// <summary>
        /// Record a plan lifecycle event (created, submitted, approved, rejected).
        /// </summary>
        public long RecordPlanEvent(
            string planId,
            string eventType,
            string actor,
            string summary,
            string detailsJson = null)
        {
            if (!ValidPlanTypes.Contains(eventType))
            {
                throw new ArgumentException($"Invalid plan event type: {eventType}. Valid: {string.Join(", ", ValidPlanTypes)}");
            }

            var severity = eventType == "PLAN_REJECTED" ? "warning" : "info";
            return InsertActivity(eventType, planId, null, actor, summary, severity, detailsJson);
        }

        /// <summary>
        /// Record a phase transition event (started, completed).
        /// </summary>
        public long RecordPhaseEvent(
            string planId,
            string phaseId,
            string eventType,
            string actor,
            string summary,
            string detailsJson = null)
        {
            if (!ValidPhaseTypes.Contains(eventType))
            {
                throw new ArgumentException($"Invalid phase event type: {eventType}. Valid: {string.Join(", ", ValidPhaseTypes)}");
            }

            return InsertActivity(eventType, planId, phaseId, actor, summary, "info", detailsJson);
        }

        /// <summary>
        /// Record a build event (started, succeeded, failed).
        /// </summary>
        public long RecordBuildEvent(
            string projectName,
            string eventType,
            string actor,
            string summary,
            string detailsJson = null)
        {
            if (!ValidBuildTypes.Contains(eventType))
            {
                throw new ArgumentException($"Invalid build event type: {eventType}. Valid: {string.Join(", ", ValidBuildTypes)}");
            }

            var severity = eventType == "BUILD_FAILED" ? "error"
                         : eventType == "BUILD_STARTED" ? "info"
                         : "info";  // BUILD_SUCCEEDED

            return InsertActivity(eventType, null, null, actor, summary, severity, detailsJson);
        }

        private long InsertActivity(
            string activityType,
            string planId,
            string phaseId,
            string actor,
            string summary,
            string severity,
            string detailsJson,
            string projectId = null)
        {
            var timestamp = DateTime.UtcNow.ToString("o");

            const string sql = @"
                INSERT INTO activity_feed (timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json, project_id)
                VALUES (@timestamp, @activityType, @planId, @phaseId, @actor, @summary, @severity, @detailsJson, @projectId);
                SELECT last_insert_rowid();
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@timestamp", timestamp);
            command.Parameters.AddWithValue("@activityType", activityType);
            command.Parameters.AddWithValue("@planId", (object)planId ?? DBNull.Value);
            command.Parameters.AddWithValue("@phaseId", (object)phaseId ?? DBNull.Value);
            command.Parameters.AddWithValue("@actor", (object)actor ?? DBNull.Value);
            command.Parameters.AddWithValue("@summary", summary);
            command.Parameters.AddWithValue("@severity", severity);
            command.Parameters.AddWithValue("@detailsJson", (object)detailsJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@projectId", (object)projectId ?? DBNull.Value);

            var id = Convert.ToInt64(command.ExecuteScalar());

            // Raise event for UI refresh
            var entry = new ActivityFeedEntry
            {
                Id = id,
                Timestamp = DateTime.Parse(timestamp),
                ActivityType = activityType,
                PlanId = planId,
                PhaseId = phaseId,
                Actor = actor,
                Summary = summary,
                Severity = severity,
                DetailsJson = detailsJson,
                ProjectId = projectId
            };
            ActivityRecorded?.Invoke(this, entry);

            System.Diagnostics.Debug.WriteLine($"[ActivityFeed] {activityType}: {summary}");

            return id;
        }

        /// <summary>
        /// Record a general activity event (task created, message sent, session started, etc.).
        /// Unlike the typed methods above, this accepts any activity type string.
        /// </summary>
        public long RecordGeneralActivity(string activityType, string actor, string summary, string severity = "info", string detailsJson = null, string projectId = null)
        {
            return InsertActivity(activityType, null, null, actor, summary, severity, detailsJson, projectId);
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Get recent activities, optionally filtered by plan and/or project.
        /// </summary>
        public List<ActivityFeedEntry> GetRecentActivities(int limit = 50, string planId = null, string projectId = null)
        {
            var entries = new List<ActivityFeedEntry>();

            var conditions = new List<string>();
            if (planId != null) conditions.Add("plan_id = @planId");
            if (projectId != null) conditions.Add("project_id = @projectId");

            var whereClause = conditions.Count > 0
                ? "WHERE " + string.Join(" AND ", conditions)
                : "";

            var sql = $@"SELECT id, timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json, project_id
                    FROM activity_feed {whereClause} ORDER BY timestamp DESC LIMIT @limit";

            // CA2100: whereClause is composed solely from the hardcoded literals "plan_id = @planId" / "project_id = @projectId";
            // all user-supplied values flow through SQLiteParameter.
            #pragma warning disable CA2100
            using var command = new SQLiteCommand(sql, _connection);
            #pragma warning restore CA2100
            command.Parameters.AddWithValue("@limit", limit);
            if (planId != null) command.Parameters.AddWithValue("@planId", planId);
            if (projectId != null) command.Parameters.AddWithValue("@projectId", projectId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }

        /// <summary>
        /// Get activities within a time window.
        /// </summary>
        public List<ActivityFeedEntry> GetActivitiesSince(DateTime since, int limit = 100)
        {
            var entries = new List<ActivityFeedEntry>();

            const string sql = @"
                SELECT id, timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json, project_id
                FROM activity_feed
                WHERE timestamp >= @since
                ORDER BY timestamp DESC
                LIMIT @limit";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@since", since.ToString("o"));
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }

        /// <summary>
        /// Get activities by type (e.g., all BUILD_FAILED events).
        /// </summary>
        public List<ActivityFeedEntry> GetActivitiesByType(string activityType, int limit = 50)
        {
            var entries = new List<ActivityFeedEntry>();

            const string sql = @"
                SELECT id, timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json, project_id
                FROM activity_feed
                WHERE activity_type = @activityType
                ORDER BY timestamp DESC
                LIMIT @limit";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@activityType", activityType);
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }

        private ActivityFeedEntry ReadEntry(SQLiteDataReader reader)
        {
            var entry = new ActivityFeedEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTime.Parse(reader.GetString(1)),
                ActivityType = reader.GetString(2),
                PlanId = reader.IsDBNull(3) ? null : reader.GetString(3),
                PhaseId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Actor = reader.IsDBNull(5) ? null : reader.GetString(5),
                Summary = reader.GetString(6),
                Severity = reader.IsDBNull(7) ? "info" : reader.GetString(7),
                DetailsJson = reader.IsDBNull(8) ? null : reader.GetString(8)
            };

            // project_id is column index 9 when selected
            if (reader.FieldCount > 9 && !reader.IsDBNull(9))
                entry.ProjectId = reader.GetString(9);

            return entry;
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            _isDisposed = true;
        }
    }

    /// <summary>
    /// Represents a single entry in the activity feed.
    /// </summary>
    public class ActivityFeedEntry
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string ActivityType { get; set; }
        public string PlanId { get; set; }
        public string PhaseId { get; set; }
        public string Actor { get; set; }
        public string Summary { get; set; }
        public string Severity { get; set; }
        public string DetailsJson { get; set; }
        public string ProjectId { get; set; }
    }
}
