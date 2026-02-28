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
            string detailsJson)
        {
            var timestamp = DateTime.UtcNow.ToString("o");

            const string sql = @"
                INSERT INTO activity_feed (timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json)
                VALUES (@timestamp, @activityType, @planId, @phaseId, @actor, @summary, @severity, @detailsJson);
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
                DetailsJson = detailsJson
            };
            ActivityRecorded?.Invoke(this, entry);

            System.Diagnostics.Debug.WriteLine($"[ActivityFeed] {activityType}: {summary}");

            return id;
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Get recent activities, optionally filtered by plan.
        /// </summary>
        public List<ActivityFeedEntry> GetRecentActivities(int limit = 50, string planId = null)
        {
            var entries = new List<ActivityFeedEntry>();

            var sql = planId != null
                ? @"SELECT id, timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json
                    FROM activity_feed WHERE plan_id = @planId ORDER BY timestamp DESC LIMIT @limit"
                : @"SELECT id, timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json
                    FROM activity_feed ORDER BY timestamp DESC LIMIT @limit";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@limit", limit);
            if (planId != null)
            {
                command.Parameters.AddWithValue("@planId", planId);
            }

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
                SELECT id, timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json
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
                SELECT id, timestamp, activity_type, plan_id, phase_id, actor, summary, severity, details_json
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
            return new ActivityFeedEntry
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
        }

        #endregion

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _isDisposed = true;
            }
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
    }
}
