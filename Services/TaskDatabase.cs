using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// SQLite database service for persisting Kanban tasks.
    /// Database is stored at %APPDATA%\multiterminal\tasks.db
    /// </summary>
    public class TaskDatabase : IDisposable
    {
        private readonly string _databasePath;
        private SQLiteConnection _connection;
        private bool _isDisposed;

        // FTS5 availability flag — checked once at init, falls back to LIKE queries if unavailable
        private bool _fts5Available;

        /// <summary>
        /// Gets the path to the tasks database.
        /// </summary>
        public static string GetDatabasePath()
        {
            var testDb = Environment.GetEnvironmentVariable("MULTITERMINAL_TEST_DB");
            if (!string.IsNullOrEmpty(testDb)) return testDb;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "multiterminal");
            return Path.Combine(folder, "tasks.db");
        }

        /// <summary>
        /// Creates a new TaskDatabase instance.
        /// </summary>
        public TaskDatabase()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "multiterminal");

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            _databasePath = Path.Combine(folder, "tasks.db");
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

            // Always run CreateSchema - all statements use IF NOT EXISTS so it's idempotent
            CreateSchema();

            // Run migrations for schema changes
            MigrateAddProjectIdToTasks();
            MigrateAddTaskSummaries();
            MigrateAddTaskStackColumns();
            MigrateAddStaleColumns();
            MigrateAddPriorityColumn();
            MigrateAddCriticalSection();
            MigrateAddChecklistToTasks();
            MigrateAddImplementationChecklistToTasks();
            MigrateAddPendingEnhancement();
            MigrateAddTeamMemberProfiles();
            MigrateAddHelperSessions();
            MigrateAddTaskDocumentation();
            MigrateCleanupProfileNullStrings();
            MigrateAddOnlineStatusToProfiles();
            MigrateAddContinuationNotesToTasks();
            MigrateAddAutoStatusToTasks();
            MigrateAddUserInbox();
            MigrateAddProjectIdsToProfiles();
            MigrateAddTaskAttachments();
            MigrateAddAgentFieldsToProfiles();
            MigrateAddTeamLeadToProfiles();
            MigrateAddSessionLineage();

            // Check FTS5 support after all tables are created
            _fts5Available = CheckFts5Available();
        }

        private void CreateSchema()
        {
            const string schema = @"
                CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    description TEXT,
                    status TEXT NOT NULL DEFAULT 'todo',
                    assignee TEXT,
                    created_by TEXT,
                    created_at DATETIME NOT NULL,
                    updated_at DATETIME NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks(status);
                CREATE INDEX IF NOT EXISTS idx_tasks_assignee ON tasks(assignee);

                CREATE TABLE IF NOT EXISTS terminal_activity (
                    terminal TEXT PRIMARY KEY,
                    status TEXT DEFAULT 'idle',
                    activity TEXT,
                    blocked_by TEXT,
                    task_id TEXT,
                    plan_id TEXT,
                    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS idx_terminal_activity_status ON terminal_activity(status);

                -- Activity feed for manager dashboard (high-level workflow events)
                CREATE TABLE IF NOT EXISTS activity_feed (
                    id INTEGER PRIMARY KEY,
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

                -- Projects table for grouping tasks
                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    created_by TEXT,
                    created_at DATETIME NOT NULL,
                    updated_at DATETIME NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_projects_name ON projects(name);

                -- Task summaries for tracking work progress and handoffs
                CREATE TABLE IF NOT EXISTS task_summaries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL,
                    summary_at DATETIME NOT NULL,
                    triggered_by TEXT NOT NULL,
                    previous_status TEXT,
                    new_status TEXT,
                    work_completed TEXT,
                    next_steps TEXT,
                    blockers TEXT,
                    notes TEXT,
                    author TEXT,
                    is_auto_generated INTEGER DEFAULT 0,
                    pending_enhancement INTEGER DEFAULT 0,
                    FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_summaries_task ON task_summaries(task_id);
                CREATE INDEX IF NOT EXISTS idx_summaries_time ON task_summaries(summary_at DESC);

                -- Task helpers for team collaboration
                CREATE TABLE IF NOT EXISTS task_helpers (
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    helper_name TEXT NOT NULL,
                    added_by TEXT NOT NULL,
                    added_at DATETIME NOT NULL,
                    FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE,
                    UNIQUE(task_id, helper_name)
                );

                CREATE INDEX IF NOT EXISTS idx_task_helpers_task ON task_helpers(task_id);
                CREATE INDEX IF NOT EXISTS idx_task_helpers_helper ON task_helpers(helper_name);

                -- Complexity analysis decisions for learnable heuristic
                CREATE TABLE IF NOT EXISTS complexity_decisions (
                    id INTEGER PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    score INTEGER NOT NULL,
                    signals_json TEXT,
                    suggested_plan INTEGER NOT NULL,
                    user_accepted INTEGER,
                    decided_at DATETIME,
                    created_at DATETIME NOT NULL,
                    FOREIGN KEY (task_id) REFERENCES tasks(id)
                );

                CREATE INDEX IF NOT EXISTS idx_complexity_task ON complexity_decisions(task_id);
                CREATE INDEX IF NOT EXISTS idx_complexity_outcome ON complexity_decisions(user_accepted);

                -- Team member profiles for rich identity information
                CREATE TABLE IF NOT EXISTS team_member_profiles (
                    id TEXT PRIMARY KEY,
                    display_name TEXT,
                    avatar_url TEXT,
                    role TEXT,
                    bio TEXT,
                    skills_json TEXT DEFAULT '[]',
                    interests_json TEXT DEFAULT '[]',
                    created_at DATETIME NOT NULL,
                    updated_at DATETIME NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_profiles_display_name ON team_member_profiles(display_name);

                -- Session lineage: tracks parent/child relationships between Claude Code sessions
                CREATE TABLE IF NOT EXISTS session_lineage (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL UNIQUE,
                    parent_session_id TEXT,
                    task_id TEXT,
                    agent_name TEXT NOT NULL,
                    session_type TEXT NOT NULL DEFAULT 'terminal',
                    summary TEXT,
                    session_file_path TEXT,
                    started_at TEXT,
                    ended_at TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    FOREIGN KEY (task_id) REFERENCES tasks(id)
                );

                CREATE INDEX IF NOT EXISTS idx_session_lineage_session ON session_lineage(session_id);
                CREATE INDEX IF NOT EXISTS idx_session_lineage_parent ON session_lineage(parent_session_id);
                CREATE INDEX IF NOT EXISTS idx_session_lineage_task ON session_lineage(task_id);
                CREATE INDEX IF NOT EXISTS idx_session_lineage_agent ON session_lineage(agent_name);

                -- Session messages: stores individual messages extracted from session JSONL files
                CREATE TABLE IF NOT EXISTS session_messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    task_id TEXT,
                    agent_name TEXT,
                    message_index INTEGER NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT,
                    tool_name TEXT,
                    timestamp TEXT,
                    FOREIGN KEY (session_id) REFERENCES session_lineage(session_id)
                );

                CREATE INDEX IF NOT EXISTS idx_session_messages_session ON session_messages(session_id);
                CREATE INDEX IF NOT EXISTS idx_session_messages_task ON session_messages(task_id);
                CREATE INDEX IF NOT EXISTS idx_session_messages_role ON session_messages(role);
            ";

            using var command = new SQLiteCommand(schema, _connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Migration to add project_id column to tasks table.
        /// </summary>
        private void MigrateAddProjectIdToTasks()
        {
            // Check if column exists by querying table info
            const string checkSql = "PRAGMA table_info(tasks)";
            bool columnExists = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("project_id", StringComparison.OrdinalIgnoreCase))
                    {
                        columnExists = true;
                        break;
                    }
                }
            }

            if (!columnExists)
            {
                const string alterSql = "ALTER TABLE tasks ADD COLUMN project_id TEXT";
                using var alterCommand = new SQLiteCommand(alterSql, _connection);
                alterCommand.ExecuteNonQuery();

                const string indexSql = "CREATE INDEX IF NOT EXISTS idx_tasks_project ON tasks(project_id)";
                using var indexCommand = new SQLiteCommand(indexSql, _connection);
                indexCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add task_summaries table if it doesn't exist.
        /// </summary>
        private void MigrateAddTaskSummaries()
        {
            // Check if table exists
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='task_summaries'";
            bool tableExists = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                tableExists = reader.Read();
            }

            if (!tableExists)
            {
                const string createSql = @"
                    CREATE TABLE task_summaries (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        task_id TEXT NOT NULL,
                        summary_at DATETIME NOT NULL,
                        triggered_by TEXT NOT NULL,
                        previous_status TEXT,
                        new_status TEXT,
                        work_completed TEXT,
                        next_steps TEXT,
                        blockers TEXT,
                        notes TEXT,
                        author TEXT,
                        is_auto_generated INTEGER DEFAULT 0,
                        FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS idx_summaries_task ON task_summaries(task_id);
                    CREATE INDEX IF NOT EXISTS idx_summaries_time ON task_summaries(summary_at DESC);
                ";
                using var createCommand = new SQLiteCommand(createSql, _connection);
                createCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add sub_status and paused_at columns to tasks table.
        /// </summary>
        private void MigrateAddTaskStackColumns()
        {
            // Check if columns exist by querying table info
            const string checkSql = "PRAGMA table_info(tasks)";
            bool subStatusExists = false;
            bool pausedAtExists = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("sub_status", StringComparison.OrdinalIgnoreCase))
                    {
                        subStatusExists = true;
                    }
                    if (columnName.Equals("paused_at", StringComparison.OrdinalIgnoreCase))
                    {
                        pausedAtExists = true;
                    }
                }
            }

            if (!subStatusExists)
            {
                const string alterSql = "ALTER TABLE tasks ADD COLUMN sub_status TEXT";
                using var alterCommand = new SQLiteCommand(alterSql, _connection);
                alterCommand.ExecuteNonQuery();

                const string indexSql = "CREATE INDEX IF NOT EXISTS idx_tasks_sub_status ON tasks(sub_status)";
                using var indexCommand = new SQLiteCommand(indexSql, _connection);
                indexCommand.ExecuteNonQuery();
            }

            if (!pausedAtExists)
            {
                const string alterSql = "ALTER TABLE tasks ADD COLUMN paused_at DATETIME";
                using var alterCommand = new SQLiteCommand(alterSql, _connection);
                alterCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add stale task tracking columns to tasks table.
        /// </summary>
        private void MigrateAddStaleColumns()
        {
            // Check if columns exist by querying table info
            const string checkSql = "PRAGMA table_info(tasks)";
            bool hasFlaggedAt = false;
            bool hasStaleLevel = false;
            bool hasStaleNotifiedAt = false;
            bool hasStaleResponse = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("flagged_stale_at", StringComparison.OrdinalIgnoreCase))
                        hasFlaggedAt = true;
                    if (columnName.Equals("stale_level", StringComparison.OrdinalIgnoreCase))
                        hasStaleLevel = true;
                    if (columnName.Equals("stale_notified_at", StringComparison.OrdinalIgnoreCase))
                        hasStaleNotifiedAt = true;
                    if (columnName.Equals("stale_response", StringComparison.OrdinalIgnoreCase))
                        hasStaleResponse = true;
                }
            }

            if (!hasFlaggedAt)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN flagged_stale_at DATETIME";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }

            if (!hasStaleLevel)
            {
                // stale_level: 0=fresh, 1=day7-flagged, 2=day14-flagged
                const string sql = "ALTER TABLE tasks ADD COLUMN stale_level INTEGER DEFAULT 0";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }

            if (!hasStaleNotifiedAt)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN stale_notified_at DATETIME";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }

            if (!hasStaleResponse)
            {
                // stale_response: 'still_relevant', 'will_close', 'reprioritized', or null
                const string sql = "ALTER TABLE tasks ADD COLUMN stale_response TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add priority column to tasks table.
        /// </summary>
        private void MigrateAddPriorityColumn()
        {
            // Check if column exists by querying table info
            const string checkSql = "PRAGMA table_info(tasks)";
            bool hasPriority = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("priority", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPriority = true;
                        break;
                    }
                }
            }

            if (!hasPriority)
            {
                // priority: 'urgent', 'normal', 'low' - default is 'normal'
                const string sql = "ALTER TABLE tasks ADD COLUMN priority TEXT DEFAULT 'normal'";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();

                const string indexSql = "CREATE INDEX IF NOT EXISTS idx_tasks_priority ON tasks(priority)";
                using var indexCmd = new SQLiteCommand(indexSql, _connection);
                indexCmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add critical section columns to terminal_activity table.
        /// Allows terminals to signal "don't interrupt me" with a bounded timeout.
        /// </summary>
        private void MigrateAddCriticalSection()
        {
            // Check if columns exist by querying table info
            const string checkSql = "PRAGMA table_info(terminal_activity)";
            bool hasInCriticalSection = false;
            bool hasCriticalSectionUntil = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("in_critical_section", StringComparison.OrdinalIgnoreCase))
                        hasInCriticalSection = true;
                    if (columnName.Equals("critical_section_until", StringComparison.OrdinalIgnoreCase))
                        hasCriticalSectionUntil = true;
                }
            }

            if (!hasInCriticalSection)
            {
                const string sql = "ALTER TABLE terminal_activity ADD COLUMN in_critical_section INTEGER DEFAULT 0";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }

            if (!hasCriticalSectionUntil)
            {
                const string sql = "ALTER TABLE terminal_activity ADD COLUMN critical_section_until DATETIME";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add checklist_json column to tasks table.
        /// Allows tasks to have checklist items for tracking sub-tasks.
        /// </summary>
        private void MigrateAddChecklistToTasks()
        {
            // Check if column exists by querying table info
            const string checkSql = "PRAGMA table_info(tasks)";
            bool hasChecklist = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("checklist_json", StringComparison.OrdinalIgnoreCase))
                    {
                        hasChecklist = true;
                        break;
                    }
                }
            }

            if (!hasChecklist)
            {
                // checklist_json: JSON array of checklist items [{"item": "...", "done": true}, ...]
                const string sql = "ALTER TABLE tasks ADD COLUMN checklist_json TEXT DEFAULT '[]'";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add implementation_checklist_json column to tasks table.
        /// Allows tasks to track what was actually built/implemented as a checklist.
        /// Separate from general checklist_json which tracks sub-tasks to do.
        /// </summary>
        private void MigrateAddImplementationChecklistToTasks()
        {
            // Check if column exists by querying table info
            const string checkSql = "PRAGMA table_info(tasks)";
            bool hasImplementationChecklist = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("implementation_checklist_json", StringComparison.OrdinalIgnoreCase))
                    {
                        hasImplementationChecklist = true;
                        break;
                    }
                }
            }

            if (!hasImplementationChecklist)
            {
                // implementation_checklist_json: JSON array of implementation checklist items [{"item": "...", "done": true}, ...]
                const string sql = "ALTER TABLE tasks ADD COLUMN implementation_checklist_json TEXT DEFAULT '[]'";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add pending_enhancement column to task_summaries table.
        /// Fixes issue where CreateSchema has the column but MigrateAddTaskSummaries migration doesn't.
        /// </summary>
        private void MigrateAddPendingEnhancement()
        {
            // Check if column exists by querying table info
            const string checkSql = "PRAGMA table_info(task_summaries)";
            bool hasPendingEnhancement = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("pending_enhancement", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPendingEnhancement = true;
                        break;
                    }
                }
            }

            if (!hasPendingEnhancement)
            {
                const string sql = "ALTER TABLE task_summaries ADD COLUMN pending_enhancement INTEGER DEFAULT 0";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add team_member_profiles table.
        /// Profiles store rich identity information (avatar, skills, interests, bio).
        /// </summary>
        private void MigrateAddTeamMemberProfiles()
        {
            // Check if table exists
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='team_member_profiles'";
            bool tableExists = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                tableExists = reader.Read();
            }

            if (!tableExists)
            {
                const string createSql = @"
                    CREATE TABLE team_member_profiles (
                        id TEXT PRIMARY KEY,
                        display_name TEXT,
                        avatar_url TEXT,
                        role TEXT,
                        bio TEXT,
                        skills_json TEXT DEFAULT '[]',
                        interests_json TEXT DEFAULT '[]',
                        created_at DATETIME NOT NULL,
                        updated_at DATETIME NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_profiles_display_name ON team_member_profiles(display_name);
                ";
                using var createCommand = new SQLiteCommand(createSql, _connection);
                createCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add helper_sessions and helper_messages tables.
        /// Tracks ephemeral native helper spawning, lifecycle, and messages.
        /// </summary>
        private void MigrateAddHelperSessions()
        {
            // Check if helper_sessions table exists
            const string checkSessionsSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='helper_sessions'";
            bool sessionsExist = false;

            using (var command = new SQLiteCommand(checkSessionsSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                sessionsExist = reader.Read();
            }

            if (!sessionsExist)
            {
                const string createSql = @"
                    CREATE TABLE helper_sessions (
                        id TEXT PRIMARY KEY,
                        task_id TEXT,
                        prompt TEXT NOT NULL,
                        spawned_by TEXT NOT NULL,
                        spawned_at DATETIME NOT NULL,
                        completed_at DATETIME,
                        status TEXT NOT NULL DEFAULT 'spawning',
                        FOREIGN KEY(task_id) REFERENCES tasks(id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_helper_sessions_task ON helper_sessions(task_id);
                    CREATE INDEX IF NOT EXISTS idx_helper_sessions_spawned_by ON helper_sessions(spawned_by);
                    CREATE INDEX IF NOT EXISTS idx_helper_sessions_status ON helper_sessions(status);
                    CREATE INDEX IF NOT EXISTS idx_helper_sessions_spawned_at ON helper_sessions(spawned_at DESC);

                    CREATE TABLE helper_messages (
                        id TEXT PRIMARY KEY,
                        helper_id TEXT NOT NULL,
                        message TEXT NOT NULL,
                        timestamp DATETIME NOT NULL,
                        FOREIGN KEY(helper_id) REFERENCES helper_sessions(id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS idx_helper_messages_helper ON helper_messages(helper_id);
                    CREATE INDEX IF NOT EXISTS idx_helper_messages_timestamp ON helper_messages(timestamp DESC);
                ";
                using var createCommand = new SQLiteCommand(createSql, _connection);
                createCommand.ExecuteNonQuery();
            }
        }

        private void MigrateAddTaskDocumentation()
        {
            // Check if columns exist by querying table info
            const string checkSql = "PRAGMA table_info(tasks)";
            bool hasPlan = false;
            bool hasImplementationSummary = false;
            bool hasTestResults = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("plan", StringComparison.OrdinalIgnoreCase))
                        hasPlan = true;
                    if (columnName.Equals("implementation_summary", StringComparison.OrdinalIgnoreCase))
                        hasImplementationSummary = true;
                    if (columnName.Equals("test_results", StringComparison.OrdinalIgnoreCase))
                        hasTestResults = true;
                }
            }

            // Add missing columns
            if (!hasPlan)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN plan TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }

            if (!hasImplementationSummary)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN implementation_summary TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }

            if (!hasTestResults)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN test_results TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Clean up string "null" values in team_member_profiles table.
        /// Replaces string 'null' with actual NULL in avatar_url, display_name, role, and bio columns.
        /// </summary>
        private void MigrateCleanupProfileNullStrings()
        {
            // Check if team_member_profiles table exists
            const string checkTableSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='team_member_profiles'";
            bool tableExists = false;

            using (var command = new SQLiteCommand(checkTableSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    tableExists = true;
                }
            }

            if (!tableExists)
                return; // Table doesn't exist yet, nothing to migrate

            // Update string "null" to NULL for avatar_url, display_name, role, and bio
            const string updateSql = @"
                UPDATE team_member_profiles
                SET
                    avatar_url = CASE WHEN avatar_url = 'null' THEN NULL ELSE avatar_url END,
                    display_name = CASE WHEN display_name = 'null' THEN NULL ELSE display_name END,
                    role = CASE WHEN role = 'null' THEN NULL ELSE role END,
                    bio = CASE WHEN bio = 'null' THEN NULL ELSE bio END
                WHERE
                    avatar_url = 'null' OR
                    display_name = 'null' OR
                    role = 'null' OR
                    bio = 'null'
            ";

            using var cmd = new SQLiteCommand(updateSql, _connection);
            int rowsUpdated = cmd.ExecuteNonQuery();

            if (rowsUpdated > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskDatabase] Cleaned up {rowsUpdated} profile(s) with string 'null' values");
            }
        }

        /// <summary>
        /// Migration to add is_online column to team_member_profiles table.
        /// Tracks online/offline status for filtering terminals in Chat and Activity panels.
        /// </summary>
        private void MigrateAddOnlineStatusToProfiles()
        {
            // Check if table exists
            const string checkTableSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='team_member_profiles'";
            bool tableExists = false;

            using (var command = new SQLiteCommand(checkTableSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    tableExists = true;
                }
            }

            if (!tableExists)
                return; // Table doesn't exist yet, migration will run after table is created

            // Check if column already exists
            const string checkColumnSql = "PRAGMA table_info(team_member_profiles)";
            bool hasIsOnline = false;

            using (var command = new SQLiteCommand(checkColumnSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("is_online", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIsOnline = true;
                        break;
                    }
                }
            }

            if (!hasIsOnline)
            {
                // Add is_online column (default FALSE - all offline until registered)
                const string sql = "ALTER TABLE team_member_profiles ADD COLUMN is_online INTEGER DEFAULT 0";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
                System.Diagnostics.Debug.WriteLine("[TaskDatabase] Added is_online column to team_member_profiles");
            }
        }

        /// <summary>
        /// Migration to add continuation_notes column to tasks table.
        /// Stores quick "pick up here" context for session handoffs.
        /// </summary>
        private void MigrateAddContinuationNotesToTasks()
        {
            const string checkSql = "PRAGMA table_info(tasks)";
            bool hasContinuationNotes = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("continuation_notes", StringComparison.OrdinalIgnoreCase))
                    {
                        hasContinuationNotes = true;
                        break;
                    }
                }
            }

            if (!hasContinuationNotes)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN continuation_notes TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        private void MigrateAddAutoStatusToTasks()
        {
            const string checkSql = "PRAGMA table_info(tasks)";
            bool hasAutoStatus = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("auto_status", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAutoStatus = true;
                        break;
                    }
                }
            }

            if (!hasAutoStatus)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN auto_status INTEGER DEFAULT 0";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Load all tasks from the database, including helpers.
        /// </summary>
        public List<KanbanTask> LoadAllTasks()
        {
            var tasks = new List<KanbanTask>();

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status
                FROM tasks
                ORDER BY created_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }

            // Load helpers for each task
            foreach (var task in tasks)
            {
                var helpers = LoadTaskHelpers(task.Id);
                task.Helpers = helpers.ConvertAll(h => h.HelperName);
            }

            return tasks;
        }

        /// <summary>
        /// Helper method to read a KanbanTask from a data reader.
        /// Expects columns: id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status
        /// </summary>
        private static KanbanTask ReadTaskFromReader(SQLiteDataReader reader)
        {
            return new KanbanTask
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Status = reader.GetString(3),
                Assignee = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = reader.GetDateTime(6),
                ProjectId = reader.IsDBNull(8) ? null : reader.GetString(8),
                SubStatus = reader.IsDBNull(9) ? null : reader.GetString(9),
                PausedAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                FlaggedStaleAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                StaleLevel = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                StaleNotifiedAt = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                StaleResponse = reader.IsDBNull(14) ? null : reader.GetString(14),
                Priority = reader.IsDBNull(15) ? "normal" : reader.GetString(15),
                ChecklistJson = reader.IsDBNull(16) ? "[]" : reader.GetString(16),
                Plan = reader.IsDBNull(17) ? null : reader.GetString(17),
                ImplementationSummary = reader.IsDBNull(18) ? null : reader.GetString(18),
                TestResults = reader.IsDBNull(19) ? null : reader.GetString(19),
                ImplementationChecklistJson = reader.IsDBNull(20) ? "[]" : reader.GetString(20),
                ContinuationNotes = reader.IsDBNull(21) ? null : reader.GetString(21),
                AutoStatus = !reader.IsDBNull(22) && reader.GetInt32(22) != 0
            };
        }

        /// <summary>
        /// Save a task (insert or update).
        /// </summary>
        public void SaveTask(KanbanTask task)
        {
            const string sql = @"
                INSERT INTO tasks (id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status)
                VALUES (@id, @title, @description, @status, @assignee, @createdBy, @createdAt, @updatedAt, @projectId, @subStatus, @pausedAt, @flaggedStaleAt, @staleLevel, @staleNotifiedAt, @staleResponse, @priority, @checklistJson, @plan, @implementationSummary, @testResults, @implementationChecklistJson, @continuationNotes, @autoStatus)
                ON CONFLICT(id) DO UPDATE SET
                    title = @title,
                    description = @description,
                    status = @status,
                    assignee = @assignee,
                    project_id = @projectId,
                    sub_status = @subStatus,
                    paused_at = @pausedAt,
                    flagged_stale_at = @flaggedStaleAt,
                    stale_level = @staleLevel,
                    stale_notified_at = @staleNotifiedAt,
                    stale_response = @staleResponse,
                    priority = @priority,
                    checklist_json = @checklistJson,
                    plan = @plan,
                    implementation_summary = @implementationSummary,
                    test_results = @testResults,
                    implementation_checklist_json = @implementationChecklistJson,
                    continuation_notes = @continuationNotes,
                    auto_status = @autoStatus,
                    updated_at = @updatedAt
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", task.Id);
            command.Parameters.AddWithValue("@title", task.Title);
            command.Parameters.AddWithValue("@description", (object)task.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", task.Status);
            command.Parameters.AddWithValue("@assignee", (object)task.Assignee ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdBy", (object)task.CreatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", task.CreatedAt);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@projectId", (object)task.ProjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("@subStatus", (object)task.SubStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("@pausedAt", (object)task.PausedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@flaggedStaleAt", (object)task.FlaggedStaleAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@staleLevel", task.StaleLevel);
            command.Parameters.AddWithValue("@staleNotifiedAt", (object)task.StaleNotifiedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@staleResponse", (object)task.StaleResponse ?? DBNull.Value);
            command.Parameters.AddWithValue("@priority", task.Priority ?? "normal");
            command.Parameters.AddWithValue("@checklistJson", task.ChecklistJson ?? "[]");
            command.Parameters.AddWithValue("@plan", (object)task.Plan ?? DBNull.Value);
            command.Parameters.AddWithValue("@implementationSummary", (object)task.ImplementationSummary ?? DBNull.Value);
            command.Parameters.AddWithValue("@testResults", (object)task.TestResults ?? DBNull.Value);
            command.Parameters.AddWithValue("@implementationChecklistJson", task.ImplementationChecklistJson ?? "[]");
            command.Parameters.AddWithValue("@continuationNotes", (object)task.ContinuationNotes ?? DBNull.Value);
            command.Parameters.AddWithValue("@autoStatus", task.AutoStatus ? 1 : 0);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Delete a task by ID.
        /// </summary>
        public void DeleteTask(string taskId)
        {
            const string sql = "DELETE FROM tasks WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", taskId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Update a task's title and description.
        /// </summary>
        /// <returns>True if a task was updated, false if task not found.</returns>
        public bool UpdateTask(string taskId, string title, string description)
        {
            const string sql = @"
                UPDATE tasks
                SET title = @title, description = @description, updated_at = @updatedAt
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", taskId);
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@description", (object)description ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Update a task's assignee.
        /// </summary>
        /// <returns>True if a task was updated, false if task not found.</returns>
        public bool UpdateTaskAssignee(string taskId, string assignee)
        {
            const string sql = @"
                UPDATE tasks
                SET assignee = @assignee, updated_at = @updatedAt
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", taskId);
            command.Parameters.AddWithValue("@assignee", (object)assignee ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Delete all tasks (for testing/reset).
        /// </summary>
        public void DeleteAllTasks()
        {
            const string sql = "DELETE FROM tasks";

            using var command = new SQLiteCommand(sql, _connection);
            command.ExecuteNonQuery();
        }

        #region Projects

        /// <summary>
        /// Load all projects from the database.
        /// </summary>
        public List<Project> LoadAllProjects()
        {
            var projects = new List<Project>();

            const string sql = @"
                SELECT id, name, description, created_by, created_at, updated_at
                FROM projects
                ORDER BY created_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                projects.Add(new Project
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    CreatedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
                    CreatedAt = reader.GetDateTime(4),
                    UpdatedAt = reader.GetDateTime(5)
                });
            }

            return projects;
        }

        /// <summary>
        /// Save a project (insert or update).
        /// </summary>
        public void SaveProject(Project project)
        {
            const string sql = @"
                INSERT INTO projects (id, name, description, created_by, created_at, updated_at)
                VALUES (@id, @name, @description, @createdBy, @createdAt, @updatedAt)
                ON CONFLICT(id) DO UPDATE SET
                    name = @name,
                    description = @description,
                    updated_at = @updatedAt
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", project.Id);
            command.Parameters.AddWithValue("@name", project.Name);
            command.Parameters.AddWithValue("@description", (object)project.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdBy", (object)project.CreatedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", project.CreatedAt);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Update a project's name and description.
        /// </summary>
        /// <returns>True if a project was updated, false if project not found.</returns>
        public bool UpdateProject(string projectId, string name, string description)
        {
            const string sql = @"
                UPDATE projects
                SET name = @name, description = @description, updated_at = @updatedAt
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", projectId);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@description", (object)description ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Delete a project by ID. Does not delete associated tasks.
        /// </summary>
        public void DeleteProject(string projectId)
        {
            const string sql = "DELETE FROM projects WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", projectId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Delete all projects (for testing/reset).
        /// </summary>
        public void DeleteAllProjects()
        {
            const string sql = "DELETE FROM projects";

            using var command = new SQLiteCommand(sql, _connection);
            command.ExecuteNonQuery();
        }

        #endregion

        #region Task Summaries

        /// <summary>
        /// Save a new task summary.
        /// </summary>
        public int SaveTaskSummary(TaskSummary summary)
        {
            const string sql = @"
                INSERT INTO task_summaries (task_id, summary_at, triggered_by, previous_status, new_status,
                    work_completed, next_steps, blockers, notes, author, is_auto_generated, pending_enhancement)
                VALUES (@taskId, @summaryAt, @triggeredBy, @previousStatus, @newStatus,
                    @workCompleted, @nextSteps, @blockers, @notes, @author, @isAutoGenerated, @pendingEnhancement);
                SELECT last_insert_rowid();
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", summary.TaskId);
            command.Parameters.AddWithValue("@summaryAt", summary.SummaryAt);
            command.Parameters.AddWithValue("@triggeredBy", summary.TriggeredBy);
            command.Parameters.AddWithValue("@previousStatus", (object)summary.PreviousStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("@newStatus", (object)summary.NewStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("@workCompleted", (object)summary.WorkCompleted ?? DBNull.Value);
            command.Parameters.AddWithValue("@nextSteps", (object)summary.NextSteps ?? DBNull.Value);
            command.Parameters.AddWithValue("@blockers", (object)summary.Blockers ?? DBNull.Value);
            command.Parameters.AddWithValue("@notes", (object)summary.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("@author", (object)summary.Author ?? DBNull.Value);
            command.Parameters.AddWithValue("@isAutoGenerated", summary.IsAutoGenerated ? 1 : 0);
            command.Parameters.AddWithValue("@pendingEnhancement", summary.PendingEnhancement ? 1 : 0);

            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// Get summaries for a specific task, newest first.
        /// </summary>
        public List<TaskSummary> GetTaskSummaries(string taskId, int limit = 10)
        {
            var summaries = new List<TaskSummary>();

            const string sql = @"
                SELECT id, task_id, summary_at, triggered_by, previous_status, new_status,
                    work_completed, next_steps, blockers, notes, author, is_auto_generated, pending_enhancement
                FROM task_summaries
                WHERE task_id = @taskId
                ORDER BY summary_at DESC
                LIMIT @limit
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@limit", limit);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                summaries.Add(ReadTaskSummary(reader));
            }

            return summaries;
        }

        /// <summary>
        /// Get the most recent summary for a task.
        /// </summary>
        public TaskSummary GetLatestSummary(string taskId)
        {
            const string sql = @"
                SELECT id, task_id, summary_at, triggered_by, previous_status, new_status,
                    work_completed, next_steps, blockers, notes, author, is_auto_generated, pending_enhancement
                FROM task_summaries
                WHERE task_id = @taskId
                ORDER BY summary_at DESC
                LIMIT 1
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadTaskSummary(reader);
            }

            return null;
        }

        /// <summary>
        /// Get recent summaries across all tasks, newest first.
        /// </summary>
        public List<TaskSummary> GetAllRecentSummaries(int limit = 20)
        {
            var summaries = new List<TaskSummary>();

            const string sql = @"
                SELECT id, task_id, summary_at, triggered_by, previous_status, new_status,
                    work_completed, next_steps, blockers, notes, author, is_auto_generated, pending_enhancement
                FROM task_summaries
                ORDER BY summary_at DESC
                LIMIT @limit
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@limit", limit);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                summaries.Add(ReadTaskSummary(reader));
            }

            return summaries;
        }

        /// <summary>
        /// Get pending summaries for a task that are awaiting enhancement.
        /// </summary>
        public List<TaskSummary> GetPendingSummaries(string taskId)
        {
            var summaries = new List<TaskSummary>();

            const string sql = @"
                SELECT id, task_id, summary_at, triggered_by, previous_status, new_status,
                    work_completed, next_steps, blockers, notes, author, is_auto_generated, pending_enhancement
                FROM task_summaries
                WHERE task_id = @taskId AND pending_enhancement = 1
                ORDER BY summary_at DESC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                summaries.Add(ReadTaskSummary(reader));
            }

            return summaries;
        }

        /// <summary>
        /// Finalize all pending summaries for a task (mark as no longer pending).
        /// Called when creating a new summary to finalize the previous draft.
        /// </summary>
        public void FinalizePendingSummaries(string taskId)
        {
            const string sql = @"
                UPDATE task_summaries
                SET pending_enhancement = 0
                WHERE task_id = @taskId AND pending_enhancement = 1
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Helper method to read a TaskSummary from a data reader.
        /// </summary>
        private static TaskSummary ReadTaskSummary(SQLiteDataReader reader)
        {
            return new TaskSummary
            {
                Id = reader.GetInt32(0),
                TaskId = reader.GetString(1),
                SummaryAt = reader.GetDateTime(2),
                TriggeredBy = reader.GetString(3),
                PreviousStatus = reader.IsDBNull(4) ? null : reader.GetString(4),
                NewStatus = reader.IsDBNull(5) ? null : reader.GetString(5),
                WorkCompleted = reader.IsDBNull(6) ? null : reader.GetString(6),
                NextSteps = reader.IsDBNull(7) ? null : reader.GetString(7),
                Blockers = reader.IsDBNull(8) ? null : reader.GetString(8),
                Notes = reader.IsDBNull(9) ? null : reader.GetString(9),
                Author = reader.IsDBNull(10) ? null : reader.GetString(10),
                IsAutoGenerated = reader.GetInt32(11) == 1,
                PendingEnhancement = reader.GetInt32(12) == 1
            };
        }

        #endregion

        #region Task Helpers

        /// <summary>
        /// Save a task helper (insert or replace).
        /// </summary>
        public void SaveTaskHelper(TaskHelper helper)
        {
            const string sql = @"
                INSERT OR REPLACE INTO task_helpers (id, task_id, helper_name, added_by, added_at)
                VALUES (@id, @taskId, @helperName, @addedBy, @addedAt)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", helper.Id);
            command.Parameters.AddWithValue("@taskId", helper.TaskId);
            command.Parameters.AddWithValue("@helperName", helper.HelperName);
            command.Parameters.AddWithValue("@addedBy", helper.AddedBy);
            command.Parameters.AddWithValue("@addedAt", helper.AddedAt);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Load all helpers for a specific task.
        /// </summary>
        public List<TaskHelper> LoadTaskHelpers(string taskId)
        {
            var helpers = new List<TaskHelper>();

            const string sql = @"
                SELECT id, task_id, helper_name, added_by, added_at
                FROM task_helpers
                WHERE task_id = @taskId
                ORDER BY added_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                helpers.Add(new TaskHelper
                {
                    Id = reader.GetString(0),
                    TaskId = reader.GetString(1),
                    HelperName = reader.GetString(2),
                    AddedBy = reader.GetString(3),
                    AddedAt = reader.GetDateTime(4)
                });
            }

            return helpers;
        }

        /// <summary>
        /// Load all tasks where a specific terminal is a helper.
        /// </summary>
        public List<TaskHelper> LoadTasksWhereHelper(string helperName)
        {
            var helpers = new List<TaskHelper>();

            const string sql = @"
                SELECT id, task_id, helper_name, added_by, added_at
                FROM task_helpers
                WHERE helper_name = @helperName
                ORDER BY added_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@helperName", helperName);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                helpers.Add(new TaskHelper
                {
                    Id = reader.GetString(0),
                    TaskId = reader.GetString(1),
                    HelperName = reader.GetString(2),
                    AddedBy = reader.GetString(3),
                    AddedAt = reader.GetDateTime(4)
                });
            }

            return helpers;
        }

        /// <summary>
        /// Remove a specific helper from a task.
        /// </summary>
        /// <returns>True if a helper was removed, false if not found.</returns>
        public bool RemoveTaskHelper(string taskId, string helperName)
        {
            const string sql = "DELETE FROM task_helpers WHERE task_id = @taskId AND helper_name = @helperName";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@helperName", helperName);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Check if someone is a helper on a specific task.
        /// </summary>
        public bool IsHelper(string taskId, string helperName)
        {
            const string sql = @"
                SELECT COUNT(1)
                FROM task_helpers
                WHERE task_id = @taskId AND helper_name = @helperName
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@helperName", helperName);

            var count = Convert.ToInt32(command.ExecuteScalar());
            return count > 0;
        }

        /// <summary>
        /// Delete all helpers for a specific task.
        /// </summary>
        public void DeleteTaskHelpers(string taskId)
        {
            const string sql = "DELETE FROM task_helpers WHERE task_id = @taskId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.ExecuteNonQuery();
        }

        #endregion

        #region Terminal Activity

        /// <summary>
        /// Save or update a terminal's activity status.
        /// </summary>
        public void SaveTerminalActivity(TerminalActivity activity)
        {
            const string sql = @"
                INSERT INTO terminal_activity (terminal, status, activity, blocked_by, task_id, plan_id, updated_at,
                                               in_critical_section, critical_section_until)
                VALUES (@terminal, @status, @activity, @blockedBy, @taskId, @planId, @updatedAt,
                        @inCriticalSection, @criticalSectionUntil)
                ON CONFLICT(terminal) DO UPDATE SET
                    status = @status,
                    activity = @activity,
                    blocked_by = @blockedBy,
                    task_id = @taskId,
                    plan_id = @planId,
                    updated_at = @updatedAt,
                    in_critical_section = @inCriticalSection,
                    critical_section_until = @criticalSectionUntil
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@terminal", activity.Terminal);
            command.Parameters.AddWithValue("@status", activity.Status ?? "idle");
            command.Parameters.AddWithValue("@activity", (object)activity.Activity ?? DBNull.Value);
            command.Parameters.AddWithValue("@blockedBy", (object)activity.BlockedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@taskId", (object)activity.TaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("@planId", (object)activity.PlanId ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@inCriticalSection", activity.InCriticalSection ? 1 : 0);
            command.Parameters.AddWithValue("@criticalSectionUntil", (object)activity.CriticalSectionTimeout ?? DBNull.Value);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get all terminal activities.
        /// </summary>
        public List<TerminalActivity> GetAllTerminalActivities()
        {
            var activities = new List<TerminalActivity>();

            const string sql = @"
                SELECT terminal, status, activity, blocked_by, task_id, plan_id, updated_at,
                       in_critical_section, critical_section_until
                FROM terminal_activity
                ORDER BY terminal ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                activities.Add(new TerminalActivity
                {
                    Terminal = reader.GetString(0),
                    Status = reader.IsDBNull(1) ? "idle" : reader.GetString(1),
                    Activity = reader.IsDBNull(2) ? null : reader.GetString(2),
                    BlockedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
                    TaskId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    PlanId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    UpdatedAt = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6).ToUniversalTime(),
                    InCriticalSection = !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                    CriticalSectionTimeout = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToUniversalTime()
                });
            }

            return activities;
        }

        /// <summary>
        /// Get all terminal activities with staleness flag computed.
        /// Staleness is determined on-demand: if updated_at is older than threshold, terminal is stale.
        /// </summary>
        public List<TerminalActivityInfo> GetTeamActivityWithStaleness()
        {
            var activities = GetAllTerminalActivities();
            var result = new List<TerminalActivityInfo>();
            var now = DateTime.UtcNow;

            foreach (var activity in activities)
            {
                var minutesSinceUpdate = (now - activity.UpdatedAt).TotalMinutes;
                var isStale = minutesSinceUpdate > TerminalActivity.StalenessThresholdMinutes;

                result.Add(new TerminalActivityInfo
                {
                    Terminal = activity.Terminal,
                    Status = isStale ? "unknown" : activity.Status,
                    Activity = activity.Activity,
                    BlockedBy = activity.BlockedBy,
                    TaskId = activity.TaskId,
                    PlanId = activity.PlanId,
                    UpdatedAt = activity.UpdatedAt,
                    IsStale = isStale
                });
            }

            return result;
        }

        /// <summary>
        /// Format a terminal's activity for display (e.g., in startup context).
        /// Returns format: "Alice (working: task)" or "Bob (unknown - last seen 12 min ago)"
        /// </summary>
        public static string FormatTerminalActivity(TerminalActivityInfo info)
        {
            var minutesAgo = (int)(DateTime.UtcNow - info.UpdatedAt).TotalMinutes;

            if (info.IsStale)
            {
                return $"{info.Terminal} (unknown - last seen {minutesAgo} min ago)";
            }

            var statusPart = info.Status;
            if (!string.IsNullOrEmpty(info.Activity))
            {
                statusPart = $"{info.Status}: {info.Activity}";
            }
            else if (info.Status == "blocked" && !string.IsNullOrEmpty(info.BlockedBy))
            {
                statusPart = $"blocked by {info.BlockedBy}";
            }

            return $"{info.Terminal} ({statusPart})";
        }

        /// <summary>
        /// Get a terminal's activity by name.
        /// </summary>
        public TerminalActivity GetTerminalActivity(string terminal)
        {
            const string sql = @"
                SELECT terminal, status, activity, blocked_by, task_id, plan_id, updated_at,
                       in_critical_section, critical_section_until
                FROM terminal_activity
                WHERE terminal = @terminal
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@terminal", terminal);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return new TerminalActivity
                {
                    Terminal = reader.GetString(0),
                    Status = reader.IsDBNull(1) ? "idle" : reader.GetString(1),
                    Activity = reader.IsDBNull(2) ? null : reader.GetString(2),
                    BlockedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
                    TaskId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    PlanId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    UpdatedAt = reader.IsDBNull(6) ? DateTime.UtcNow : reader.GetDateTime(6).ToUniversalTime(),
                    InCriticalSection = !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                    CriticalSectionTimeout = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToUniversalTime()
                };
            }

            return null;
        }

        #endregion

        #region Task Helpers

        /// <summary>
        /// Add a helper to a task.
        /// </summary>
        /// <returns>The helper assignment if successful, null if helper already exists or task not found.</returns>
        public TaskHelper AddTaskHelper(string taskId, string helperName, string addedBy)
        {
            // First verify the task exists
            const string checkTaskSql = "SELECT COUNT(*) FROM tasks WHERE id = @taskId";
            using (var checkCmd = new SQLiteCommand(checkTaskSql, _connection))
            {
                checkCmd.Parameters.AddWithValue("@taskId", taskId);
                var count = Convert.ToInt32(checkCmd.ExecuteScalar());
                if (count == 0) return null;
            }

            var helper = new TaskHelper
            {
                TaskId = taskId,
                HelperName = helperName,
                AddedBy = addedBy,
                AddedAt = DateTime.UtcNow
            };

            const string sql = @"
                INSERT OR IGNORE INTO task_helpers (id, task_id, helper_name, added_by, added_at)
                VALUES (@id, @taskId, @helperName, @addedBy, @addedAt)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", helper.Id);
            command.Parameters.AddWithValue("@taskId", helper.TaskId);
            command.Parameters.AddWithValue("@helperName", helper.HelperName);
            command.Parameters.AddWithValue("@addedBy", helper.AddedBy);
            command.Parameters.AddWithValue("@addedAt", helper.AddedAt);

            var rowsAffected = command.ExecuteNonQuery();
            return rowsAffected > 0 ? helper : null;
        }

        /// <summary>
        /// Get all helpers for a task.
        /// </summary>
        public List<TaskHelper> GetTaskHelpers(string taskId)
        {
            var helpers = new List<TaskHelper>();

            const string sql = @"
                SELECT id, task_id, helper_name, added_by, added_at
                FROM task_helpers
                WHERE task_id = @taskId
                ORDER BY added_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                helpers.Add(new TaskHelper
                {
                    Id = reader.GetString(0),
                    TaskId = reader.GetString(1),
                    HelperName = reader.GetString(2),
                    AddedBy = reader.IsDBNull(3) ? null : reader.GetString(3),
                    AddedAt = reader.GetDateTime(4)
                });
            }

            return helpers;
        }

        /// <summary>
        /// Check if a terminal is a helper on a task.
        /// </summary>
        public bool IsTaskHelper(string taskId, string helperName)
        {
            const string sql = @"
                SELECT COUNT(*) FROM task_helpers
                WHERE task_id = @taskId AND helper_name = @helperName
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@helperName", helperName);

            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        /// <summary>
        /// Get all tasks where a terminal is a helper.
        /// </summary>
        public List<string> GetTasksWhereHelper(string helperName)
        {
            var taskIds = new List<string>();

            const string sql = @"
                SELECT task_id FROM task_helpers
                WHERE helper_name = @helperName
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@helperName", helperName);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                taskIds.Add(reader.GetString(0));
            }

            return taskIds;
        }

        /// <summary>
        /// Get a task by ID.
        /// </summary>
        public KanbanTask GetTask(string taskId)
        {
            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status
                FROM tasks
                WHERE id = @taskId
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadTaskFromReader(reader);
            }

            return null;
        }

        #endregion

        #region Stale Task Tracking

        /// <summary>
        /// Get paused tasks that are older than 7 days and haven't been flagged yet (stale_level = 0).
        /// These tasks need Day 7 notification: "Still relevant?"
        /// </summary>
        public List<KanbanTask> GetStaleTasks()
        {
            var tasks = new List<KanbanTask>();
            var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status
                FROM tasks
                WHERE sub_status = 'paused'
                  AND paused_at < @sevenDaysAgo
                  AND (stale_level = 0 OR stale_level IS NULL)
                ORDER BY paused_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@sevenDaysAgo", sevenDaysAgo);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }

            return tasks;
        }

        /// <summary>
        /// Get paused tasks that are older than 14 days, already at stale_level 1, and haven't responded.
        /// These tasks need Day 14 notification: "Close or re-prioritize?"
        /// </summary>
        public List<KanbanTask> GetCriticalStaleTasks()
        {
            var tasks = new List<KanbanTask>();
            var fourteenDaysAgo = DateTime.UtcNow.AddDays(-14);

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status
                FROM tasks
                WHERE sub_status = 'paused'
                  AND paused_at < @fourteenDaysAgo
                  AND stale_level = 1
                  AND (stale_response IS NULL OR stale_response = '')
                ORDER BY paused_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@fourteenDaysAgo", fourteenDaysAgo);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }

            return tasks;
        }

        /// <summary>
        /// Update the stale level of a task and record when it was flagged/notified.
        /// </summary>
        /// <param name="taskId">The task ID to update.</param>
        /// <param name="level">The new stale level (0=fresh, 1=day7-flagged, 2=day14-flagged).</param>
        /// <returns>True if task was updated, false if task not found.</returns>
        public bool UpdateStaleLevel(string taskId, int level)
        {
            const string sql = @"
                UPDATE tasks
                SET stale_level = @level,
                    flagged_stale_at = @flaggedAt,
                    stale_notified_at = @notifiedAt,
                    updated_at = @updatedAt
                WHERE id = @taskId
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@level", level);
            command.Parameters.AddWithValue("@flaggedAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@notifiedAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Record the assignee's response to a stale task notification.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="response">The response: 'still_relevant', 'will_close', or 'reprioritized'.</param>
        /// <returns>True if task was updated, false if task not found.</returns>
        public bool RecordStaleResponse(string taskId, string response)
        {
            const string sql = @"
                UPDATE tasks
                SET stale_response = @response,
                    updated_at = @updatedAt
                WHERE id = @taskId
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@response", response);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Clear stale tracking when a task is un-paused or becomes active again.
        /// Resets stale_level to 0 and clears stale_response.
        /// </summary>
        /// <param name="taskId">The task ID to clear.</param>
        /// <returns>True if task was updated, false if task not found.</returns>
        public bool ClearStaleTracking(string taskId)
        {
            const string sql = @"
                UPDATE tasks
                SET stale_level = 0,
                    flagged_stale_at = NULL,
                    stale_notified_at = NULL,
                    stale_response = NULL,
                    updated_at = @updatedAt
                WHERE id = @taskId
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Get paused tasks that have been paused for at least the specified number of days.
        /// Used by StaleTaskService for scheduled checks.
        /// </summary>
        /// <param name="minDaysPaused">Minimum days paused to be considered stale.</param>
        /// <returns>List of stale paused tasks.</returns>
        public List<KanbanTask> GetStalePausedTasks(int minDaysPaused)
        {
            var tasks = new List<KanbanTask>();
            var cutoffDate = DateTime.UtcNow.AddDays(-minDaysPaused);

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at,
                       project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status
                FROM tasks
                WHERE sub_status = 'paused' AND paused_at IS NOT NULL AND paused_at <= @cutoffDate
                ORDER BY paused_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@cutoffDate", cutoffDate);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }

            return tasks;
        }

        /// <summary>
        /// Get stale tasks for a specific terminal (assignee).
        /// </summary>
        /// <param name="terminalName">The terminal name (assignee) to filter by.</param>
        /// <returns>List of stale tasks assigned to this terminal.</returns>
        public List<KanbanTask> GetStaleTasksForTerminal(string terminalName)
        {
            var tasks = new List<KanbanTask>();

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at,
                       project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status
                FROM tasks
                WHERE assignee = @assignee AND stale_level > 0
                ORDER BY stale_level DESC, paused_at ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@assignee", terminalName);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tasks.Add(ReadTaskFromReader(reader));
            }

            return tasks;
        }

        /// <summary>
        /// Flag a task as stale with the specified level and record the notification time.
        /// Alias for UpdateStaleLevel, used by StaleTaskService.
        /// </summary>
        /// <param name="taskId">The task ID to flag.</param>
        /// <param name="staleLevel">The stale level (1 = day 7 warning, 2 = day 14 urgent).</param>
        public void FlagTaskAsStale(string taskId, int staleLevel)
        {
            UpdateStaleLevel(taskId, staleLevel);
        }

        /// <summary>
        /// Clear the stale flag from a task (user acknowledged it's still relevant).
        /// Resets stale_level to 0 and paused_at to now so stale timer restarts.
        /// </summary>
        /// <param name="taskId">The task ID to clear.</param>
        public void ClearStaleFlag(string taskId)
        {
            var now = DateTime.UtcNow;

            const string sql = @"
                UPDATE tasks
                SET stale_level = 0,
                    stale_response = 'still_relevant',
                    paused_at = @now,
                    updated_at = @now
                WHERE id = @taskId
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@now", now);

            command.ExecuteNonQuery();
        }

        #endregion

        #region Complexity Decisions

        /// <summary>
        /// Record a complexity analysis for a task.
        /// </summary>
        /// <param name="taskId">The task ID that was analyzed.</param>
        /// <param name="score">The complexity score computed.</param>
        /// <param name="signals">List of signals that contributed to the score.</param>
        /// <param name="suggestedPlan">Whether the system suggested creating a plan.</param>
        public void RecordComplexityAnalysis(string taskId, int score, List<string> signals, bool suggestedPlan)
        {
            var signalsJson = signals != null && signals.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(signals)
                : null;

            const string sql = @"
                INSERT INTO complexity_decisions (task_id, score, signals_json, suggested_plan, created_at)
                VALUES (@taskId, @score, @signalsJson, @suggestedPlan, @createdAt)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@score", score);
            command.Parameters.AddWithValue("@signalsJson", (object)signalsJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@suggestedPlan", suggestedPlan ? 1 : 0);
            command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Record the user's decision on a complexity analysis suggestion.
        /// Updates the most recent pending decision for the given task.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <param name="accepted">True if user accepted the plan suggestion, false if declined.</param>
        /// <returns>True if a pending decision was found and updated, false otherwise.</returns>
        public bool RecordComplexityDecision(string taskId, bool accepted)
        {
            // Find the most recent pending decision for this task
            const string sql = @"
                UPDATE complexity_decisions
                SET user_accepted = @accepted, decided_at = @decidedAt
                WHERE id = (
                    SELECT id FROM complexity_decisions
                    WHERE task_id = @taskId AND user_accepted IS NULL
                    ORDER BY created_at DESC
                    LIMIT 1
                )
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@accepted", accepted ? 1 : 0);
            command.Parameters.AddWithValue("@decidedAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Get statistics for tuning the complexity heuristic.
        /// </summary>
        /// <returns>Statistics about complexity analysis decisions.</returns>
        public ComplexityStats GetComplexityStats()
        {
            var stats = new ComplexityStats();

            const string sql = @"
                SELECT
                    COUNT(*) as total_analyzed,
                    SUM(CASE WHEN suggested_plan = 1 THEN 1 ELSE 0 END) as plans_suggested,
                    SUM(CASE WHEN user_accepted = 1 THEN 1 ELSE 0 END) as plans_accepted,
                    SUM(CASE WHEN user_accepted = 0 THEN 1 ELSE 0 END) as plans_declined
                FROM complexity_decisions
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                stats.TotalAnalyzed = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                stats.PlansSuggested = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                stats.PlansAccepted = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                stats.PlansDeclined = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            }

            return stats;
        }

        #endregion

        #region Team Member Profiles

        /// <summary>
        /// Save a team member profile (insert or update).
        /// </summary>
        public void SaveProfile(TeamMemberProfile profile)
        {
            const string sql = @"
                INSERT INTO team_member_profiles (id, display_name, avatar_url, role, bio, skills_json, interests_json, project_ids_json, is_online, agent_instructions, preferred_model, created_at, updated_at, is_team_lead)
                VALUES (@id, @displayName, @avatarUrl, @role, @bio, @skillsJson, @interestsJson, @projectIdsJson, @isOnline, @agentInstructions, @preferredModel, @createdAt, @updatedAt, @isTeamLead)
                ON CONFLICT(id) DO UPDATE SET
                    display_name = @displayName,
                    avatar_url = @avatarUrl,
                    role = @role,
                    bio = @bio,
                    skills_json = @skillsJson,
                    interests_json = @interestsJson,
                    project_ids_json = @projectIdsJson,
                    is_online = @isOnline,
                    agent_instructions = @agentInstructions,
                    preferred_model = @preferredModel,
                    updated_at = @updatedAt,
                    is_team_lead = @isTeamLead
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", profile.Id);
            command.Parameters.AddWithValue("@displayName", (object)profile.DisplayName ?? DBNull.Value);
            command.Parameters.AddWithValue("@avatarUrl", (object)profile.AvatarUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@role", (object)profile.Role ?? DBNull.Value);
            command.Parameters.AddWithValue("@bio", (object)profile.Bio ?? DBNull.Value);
            command.Parameters.AddWithValue("@skillsJson", profile.SkillsJson ?? "[]");
            command.Parameters.AddWithValue("@interestsJson", profile.InterestsJson ?? "[]");
            command.Parameters.AddWithValue("@projectIdsJson", profile.ProjectIdsJson ?? "[]");
            command.Parameters.AddWithValue("@isOnline", profile.IsOnline ? 1 : 0);
            command.Parameters.AddWithValue("@agentInstructions", (object)profile.AgentInstructions ?? DBNull.Value);
            command.Parameters.AddWithValue("@preferredModel", (object)profile.PreferredModel ?? "sonnet");
            command.Parameters.AddWithValue("@createdAt", profile.CreatedAt);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@isTeamLead", profile.IsTeamLead ? 1 : 0);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get a team member profile by ID.
        /// </summary>
        public TeamMemberProfile GetProfile(string profileId)
        {
            const string sql = @"
                SELECT id, display_name, avatar_url, role, bio, skills_json, interests_json, project_ids_json, is_online, created_at, updated_at, agent_instructions, preferred_model, is_team_lead
                FROM team_member_profiles
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", profileId);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadProfileFromReader(reader);
            }

            return null;
        }

        /// <summary>
        /// Load all team member profiles.
        /// </summary>
        public List<TeamMemberProfile> LoadAllProfiles()
        {
            var profiles = new List<TeamMemberProfile>();

            const string sql = @"
                SELECT id, display_name, avatar_url, role, bio, skills_json, interests_json, project_ids_json, is_online, created_at, updated_at, agent_instructions, preferred_model, is_team_lead
                FROM team_member_profiles
                ORDER BY display_name ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                profiles.Add(ReadProfileFromReader(reader));
            }

            return profiles;
        }

        /// <summary>
        /// Delete a team member profile by ID.
        /// </summary>
        public bool DeleteProfile(string profileId)
        {
            const string sql = "DELETE FROM team_member_profiles WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", profileId);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Set a profile's online status to true.
        /// </summary>
        public void SetProfileOnline(string profileId)
        {
            const string sql = "UPDATE team_member_profiles SET is_online = 1, updated_at = @updatedAt WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", profileId);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Set a profile's online status to false.
        /// </summary>
        public void SetProfileOffline(string profileId)
        {
            const string sql = "UPDATE team_member_profiles SET is_online = 0, updated_at = @updatedAt WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", profileId);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Set all profiles to offline. Called on MultiTerminal startup.
        /// </summary>
        public void SetAllProfilesOffline()
        {
            const string sql = "UPDATE team_member_profiles SET is_online = 0, updated_at = @updatedAt";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);

            int rowsUpdated = command.ExecuteNonQuery();
            System.Diagnostics.Debug.WriteLine($"[TaskDatabase] Set {rowsUpdated} profile(s) to offline on startup");
        }

        /// <summary>
        /// Helper method to read a TeamMemberProfile from a data reader.
        /// </summary>
        private static TeamMemberProfile ReadProfileFromReader(SQLiteDataReader reader)
        {
            return new TeamMemberProfile
            {
                Id = reader.GetString(0),
                DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                AvatarUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                Role = reader.IsDBNull(3) ? null : reader.GetString(3),
                Bio = reader.IsDBNull(4) ? null : reader.GetString(4),
                SkillsJson = reader.IsDBNull(5) ? "[]" : reader.GetString(5),
                InterestsJson = reader.IsDBNull(6) ? "[]" : reader.GetString(6),
                ProjectIdsJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7),
                IsOnline = !reader.IsDBNull(8) && reader.GetInt32(8) == 1,
                CreatedAt = reader.GetDateTime(9),
                UpdatedAt = reader.GetDateTime(10),
                AgentInstructions = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
                PreferredModel = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : "sonnet",
                IsTeamLead = reader.FieldCount > 13 && !reader.IsDBNull(13) && reader.GetInt32(13) == 1
            };
        }

        #endregion

        #region Helper Sessions

        /// <summary>
        /// Save a helper session (insert or update).
        /// </summary>
        public void SaveHelperSession(HelperSession session)
        {
            const string sql = @"
                INSERT INTO helper_sessions (id, task_id, prompt, spawned_by, spawned_at, completed_at, status)
                VALUES (@id, @taskId, @prompt, @spawnedBy, @spawnedAt, @completedAt, @status)
                ON CONFLICT(id) DO UPDATE SET
                    status = @status,
                    completed_at = @completedAt
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", session.Id);
            command.Parameters.AddWithValue("@taskId", (object)session.TaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("@prompt", session.Prompt);
            command.Parameters.AddWithValue("@spawnedBy", session.SpawnedBy);
            command.Parameters.AddWithValue("@spawnedAt", session.SpawnedAt);
            command.Parameters.AddWithValue("@completedAt", (object)session.CompletedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", session.Status);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get a helper session by ID.
        /// </summary>
        public HelperSession GetHelperSession(string helperId)
        {
            const string sql = @"
                SELECT id, task_id, prompt, spawned_by, spawned_at, completed_at, status
                FROM helper_sessions
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", helperId);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadHelperSessionFromReader(reader);
            }

            return null;
        }

        /// <summary>
        /// Get all active helper sessions (not completed).
        /// </summary>
        public List<HelperSession> GetActiveHelperSessions()
        {
            var sessions = new List<HelperSession>();

            const string sql = @"
                SELECT id, task_id, prompt, spawned_by, spawned_at, completed_at, status
                FROM helper_sessions
                WHERE status IN ('spawning', 'working')
                ORDER BY spawned_at DESC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                sessions.Add(ReadHelperSessionFromReader(reader));
            }

            return sessions;
        }

        /// <summary>
        /// Update helper session status.
        /// </summary>
        public bool UpdateHelperStatus(string helperId, string status, DateTime? completedAt = null)
        {
            const string sql = @"
                UPDATE helper_sessions
                SET status = @status, completed_at = @completedAt
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", helperId);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@completedAt", (object)completedAt ?? DBNull.Value);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Save a helper message.
        /// </summary>
        public void SaveHelperMessage(HelperMessage message)
        {
            const string sql = @"
                INSERT INTO helper_messages (id, helper_id, message, timestamp)
                VALUES (@id, @helperId, @message, @timestamp)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", message.Id);
            command.Parameters.AddWithValue("@helperId", message.HelperId);
            command.Parameters.AddWithValue("@message", message.Message);
            command.Parameters.AddWithValue("@timestamp", message.Timestamp);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get all messages for a helper session.
        /// </summary>
        public List<HelperMessage> GetHelperMessages(string helperId)
        {
            var messages = new List<HelperMessage>();

            const string sql = @"
                SELECT id, helper_id, message, timestamp
                FROM helper_messages
                WHERE helper_id = @helperId
                ORDER BY timestamp ASC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@helperId", helperId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                messages.Add(new HelperMessage
                {
                    Id = reader.GetString(0),
                    HelperId = reader.GetString(1),
                    Message = reader.GetString(2),
                    Timestamp = reader.GetDateTime(3)
                });
            }

            return messages;
        }

        /// <summary>
        /// Helper method to read a HelperSession from a data reader.
        /// </summary>
        private static HelperSession ReadHelperSessionFromReader(SQLiteDataReader reader)
        {
            return new HelperSession
            {
                Id = reader.GetString(0),
                TaskId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Prompt = reader.GetString(2),
                SpawnedBy = reader.GetString(3),
                SpawnedAt = reader.GetDateTime(4),
                CompletedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                Status = reader.GetString(6)
            };
        }

        #endregion

        #region User Inbox

        /// <summary>
        /// Migration: Create user_inbox table for notification inbox.
        /// </summary>
        private void MigrateAddUserInbox()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='user_inbox'";
            bool tableExists = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            {
                var result = command.ExecuteScalar();
                tableExists = result != null;
            }

            if (!tableExists)
            {
                const string sql = @"
                    CREATE TABLE user_inbox (
                        id TEXT PRIMARY KEY,
                        user_id TEXT NOT NULL,
                        task_id TEXT NOT NULL,
                        task_title TEXT,
                        checklist_item_index INTEGER,
                        checklist_item_name TEXT,
                        type TEXT NOT NULL,
                        summary TEXT NOT NULL,
                        created_at DATETIME NOT NULL,
                        created_by TEXT NOT NULL,
                        read_at DATETIME,
                        reply_text TEXT,
                        replied_at DATETIME,
                        FOREIGN KEY (task_id) REFERENCES tasks(id) ON DELETE CASCADE
                    );

                    CREATE INDEX idx_inbox_user ON user_inbox(user_id);
                    CREATE INDEX idx_inbox_user_unread ON user_inbox(user_id, read_at);
                    CREATE INDEX idx_inbox_task ON user_inbox(task_id);
                    CREATE INDEX idx_inbox_created ON user_inbox(created_at DESC);
                    CREATE INDEX idx_inbox_type ON user_inbox(type);
                ";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add project_ids_json column to team_member_profiles table.
        /// Links users to projects they are assigned to.
        /// </summary>
        private void MigrateAddProjectIdsToProfiles()
        {
            const string checkSql = "PRAGMA table_info(team_member_profiles)";
            bool columnExists = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1) == "project_ids_json")
                    {
                        columnExists = true;
                        break;
                    }
                }
            }

            if (!columnExists)
            {
                const string sql = "ALTER TABLE team_member_profiles ADD COLUMN project_ids_json TEXT DEFAULT '[]'";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add agent_instructions and preferred_model columns to team_member_profiles.
        /// These fields support Team Agent spawning with profile-based identity.
        /// </summary>
        private void MigrateAddAgentFieldsToProfiles()
        {
            const string checkSql = "PRAGMA table_info(team_member_profiles)";
            bool hasAgentInstructions = false;
            bool hasPreferredModel = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var col = reader.GetString(1);
                    if (col == "agent_instructions") hasAgentInstructions = true;
                    if (col == "preferred_model") hasPreferredModel = true;
                }
            }

            if (!hasAgentInstructions)
            {
                const string sql = "ALTER TABLE team_member_profiles ADD COLUMN agent_instructions TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }

            if (!hasPreferredModel)
            {
                const string sql = "ALTER TABLE team_member_profiles ADD COLUMN preferred_model TEXT DEFAULT 'sonnet'";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add is_team_lead column to team_member_profiles.
        /// Designates agents as team leads, triggering special naming on spawn.
        /// </summary>
        private void MigrateAddTeamLeadToProfiles()
        {
            const string checkSql = "PRAGMA table_info(team_member_profiles)";
            bool hasIsTeamLead = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1).Equals("is_team_lead", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIsTeamLead = true;
                        break;
                    }
                }
            }

            if (!hasIsTeamLead)
            {
                const string sql = "ALTER TABLE team_member_profiles ADD COLUMN is_team_lead INTEGER DEFAULT 0";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
                System.Diagnostics.Debug.WriteLine("[TaskDatabase] Added is_team_lead column to team_member_profiles");
            }
        }

        /// <summary>
        /// Migration to add task_attachments table for image attachments on tasks and checklist items.
        /// </summary>
        private void MigrateAddTaskAttachments()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='task_attachments'";
            bool tableExists = false;

            using (var command = new SQLiteCommand(checkSql, _connection))
            {
                var result = command.ExecuteScalar();
                tableExists = result != null;
            }

            if (!tableExists)
            {
                const string createSql = @"
                    CREATE TABLE IF NOT EXISTS task_attachments (
                        id TEXT PRIMARY KEY,
                        task_id TEXT NOT NULL,
                        checklist_item_index INTEGER DEFAULT -1,
                        file_name TEXT NOT NULL,
                        stored_file_name TEXT NOT NULL,
                        mime_type TEXT NOT NULL,
                        file_size_bytes INTEGER DEFAULT 0,
                        added_by TEXT,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE INDEX IF NOT EXISTS idx_task_attachments_task ON task_attachments(task_id);
                    CREATE INDEX IF NOT EXISTS idx_task_attachments_task_item ON task_attachments(task_id, checklist_item_index);
                ";
                using var createCommand = new SQLiteCommand(createSql, _connection);
                createCommand.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Save an inbox message (insert or update).
        /// </summary>
        public void SaveInboxMessage(InboxMessage message)
        {
            const string sql = @"
                INSERT INTO user_inbox (id, user_id, task_id, task_title, checklist_item_index,
                    checklist_item_name, type, summary, created_at, created_by, read_at, reply_text, replied_at)
                VALUES (@id, @userId, @taskId, @taskTitle, @checklistItemIndex,
                    @checklistItemName, @type, @summary, @createdAt, @createdBy, @readAt, @replyText, @repliedAt)
                ON CONFLICT(id) DO UPDATE SET
                    read_at = @readAt,
                    reply_text = @replyText,
                    replied_at = @repliedAt
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", message.Id);
            command.Parameters.AddWithValue("@userId", message.UserId);
            command.Parameters.AddWithValue("@taskId", message.TaskId);
            command.Parameters.AddWithValue("@taskTitle", (object)message.TaskTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("@checklistItemIndex", (object)message.ChecklistItemIndex ?? DBNull.Value);
            command.Parameters.AddWithValue("@checklistItemName", (object)message.ChecklistItemName ?? DBNull.Value);
            command.Parameters.AddWithValue("@type", message.Type);
            command.Parameters.AddWithValue("@summary", message.Summary);
            command.Parameters.AddWithValue("@createdAt", message.CreatedAt);
            command.Parameters.AddWithValue("@createdBy", message.CreatedBy);
            command.Parameters.AddWithValue("@readAt", (object)message.ReadAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@replyText", (object)message.ReplyText ?? DBNull.Value);
            command.Parameters.AddWithValue("@repliedAt", (object)message.RepliedAt ?? DBNull.Value);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get inbox messages for a user, newest first.
        /// </summary>
        public List<InboxMessage> GetInboxMessages(string userId, bool unreadOnly = false, int limit = 50)
        {
            var messages = new List<InboxMessage>();

            var sql = @"
                SELECT id, user_id, task_id, task_title, checklist_item_index,
                    checklist_item_name, type, summary, created_at, created_by, read_at, reply_text, replied_at
                FROM user_inbox
                WHERE user_id = @userId
            ";

            if (unreadOnly)
            {
                sql += " AND read_at IS NULL";
            }

            sql += " ORDER BY created_at DESC LIMIT @limit";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@limit", limit);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                messages.Add(ReadInboxMessageFromReader(reader));
            }

            return messages;
        }

        /// <summary>
        /// Get unread count for a user.
        /// </summary>
        public int GetInboxUnreadCount(string userId)
        {
            const string sql = "SELECT COUNT(*) FROM user_inbox WHERE user_id = @userId AND read_at IS NULL";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@userId", userId);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// Get a single inbox message by ID.
        /// </summary>
        public InboxMessage GetInboxMessage(string messageId)
        {
            const string sql = @"
                SELECT id, user_id, task_id, task_title, checklist_item_index,
                    checklist_item_name, type, summary, created_at, created_by, read_at, reply_text, replied_at
                FROM user_inbox
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", messageId);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadInboxMessageFromReader(reader);
            }
            return null;
        }

        /// <summary>
        /// Mark an inbox message as read.
        /// </summary>
        public bool MarkInboxRead(string messageId)
        {
            const string sql = "UPDATE user_inbox SET read_at = @readAt WHERE id = @id AND read_at IS NULL";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", messageId);
            command.Parameters.AddWithValue("@readAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Mark all inbox messages as read for a user.
        /// </summary>
        public int MarkAllInboxRead(string userId)
        {
            const string sql = "UPDATE user_inbox SET read_at = @readAt WHERE user_id = @userId AND read_at IS NULL";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@readAt", DateTime.UtcNow);

            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Add a reply to an inbox message.
        /// </summary>
        public bool ReplyToInboxMessage(string messageId, string replyText)
        {
            const string sql = @"
                UPDATE user_inbox
                SET reply_text = @replyText, replied_at = @repliedAt, read_at = COALESCE(read_at, @readAt)
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", messageId);
            command.Parameters.AddWithValue("@replyText", replyText);
            command.Parameters.AddWithValue("@repliedAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@readAt", DateTime.UtcNow);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Helper method to read an InboxMessage from a data reader.
        /// </summary>
        private static InboxMessage ReadInboxMessageFromReader(SQLiteDataReader reader)
        {
            return new InboxMessage
            {
                Id = reader.GetString(0),
                UserId = reader.GetString(1),
                TaskId = reader.GetString(2),
                TaskTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
                ChecklistItemIndex = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                ChecklistItemName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Type = reader.GetString(6),
                Summary = reader.GetString(7),
                CreatedAt = reader.GetDateTime(8),
                CreatedBy = reader.GetString(9),
                ReadAt = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                ReplyText = reader.IsDBNull(11) ? null : reader.GetString(11),
                RepliedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12)
            };
        }

        #endregion

        #region Task Attachments

        /// <summary>
        /// Save an attachment record to the database.
        /// </summary>
        public void SaveAttachment(TaskAttachment attachment)
        {
            const string sql = @"
                INSERT INTO task_attachments (id, task_id, checklist_item_index, file_name, stored_file_name, mime_type, file_size_bytes, added_by, created_at)
                VALUES (@id, @taskId, @checklistItemIndex, @fileName, @storedFileName, @mimeType, @fileSizeBytes, @addedBy, @createdAt)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", attachment.Id);
            command.Parameters.AddWithValue("@taskId", attachment.TaskId);
            command.Parameters.AddWithValue("@checklistItemIndex", attachment.ChecklistItemIndex);
            command.Parameters.AddWithValue("@fileName", attachment.FileName);
            command.Parameters.AddWithValue("@storedFileName", attachment.StoredFileName);
            command.Parameters.AddWithValue("@mimeType", attachment.MimeType);
            command.Parameters.AddWithValue("@fileSizeBytes", attachment.FileSizeBytes);
            command.Parameters.AddWithValue("@addedBy", (object)attachment.AddedBy ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", attachment.CreatedAt);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get attachments for a task, optionally filtered by checklist item index.
        /// </summary>
        /// <param name="taskId">The task ID to get attachments for.</param>
        /// <param name="checklistItemIndex">If provided, only return attachments for this checklist item index. If null, returns all attachments for the task.</param>
        public List<TaskAttachment> GetAttachments(string taskId, int? checklistItemIndex = null)
        {
            var attachments = new List<TaskAttachment>();

            string sql;
            if (checklistItemIndex.HasValue)
            {
                sql = @"
                    SELECT id, task_id, checklist_item_index, file_name, stored_file_name, mime_type, file_size_bytes, added_by, created_at
                    FROM task_attachments
                    WHERE task_id = @taskId AND checklist_item_index = @checklistItemIndex
                    ORDER BY created_at ASC
                ";
            }
            else
            {
                sql = @"
                    SELECT id, task_id, checklist_item_index, file_name, stored_file_name, mime_type, file_size_bytes, added_by, created_at
                    FROM task_attachments
                    WHERE task_id = @taskId
                    ORDER BY created_at ASC
                ";
            }

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            if (checklistItemIndex.HasValue)
            {
                command.Parameters.AddWithValue("@checklistItemIndex", checklistItemIndex.Value);
            }
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                attachments.Add(ReadAttachmentFromReader(reader));
            }

            return attachments;
        }

        /// <summary>
        /// Get a single attachment by ID.
        /// </summary>
        public TaskAttachment GetAttachmentById(string attachmentId)
        {
            const string sql = @"
                SELECT id, task_id, checklist_item_index, file_name, stored_file_name, mime_type, file_size_bytes, added_by, created_at
                FROM task_attachments
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", attachmentId);
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                return ReadAttachmentFromReader(reader);
            }

            return null;
        }

        /// <summary>
        /// Delete a single attachment by ID.
        /// </summary>
        public bool DeleteAttachment(string attachmentId)
        {
            const string sql = "DELETE FROM task_attachments WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", attachmentId);

            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Delete all attachments for a task.
        /// </summary>
        public void DeleteAttachmentsForTask(string taskId)
        {
            const string sql = "DELETE FROM task_attachments WHERE task_id = @taskId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Delete all attachments for a specific checklist item on a task.
        /// </summary>
        public void DeleteAttachmentsForChecklistItem(string taskId, int itemIndex)
        {
            const string sql = "DELETE FROM task_attachments WHERE task_id = @taskId AND checklist_item_index = @itemIndex";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@itemIndex", itemIndex);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Update attachment indexes when a checklist item is deleted.
        /// Decrements checklist_item_index for all attachments with an index greater than the deleted item.
        /// </summary>
        public void UpdateAttachmentIndexes(string taskId, int deletedIndex)
        {
            const string sql = @"
                UPDATE task_attachments
                SET checklist_item_index = checklist_item_index - 1
                WHERE task_id = @taskId AND checklist_item_index > @deletedIndex
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            command.Parameters.AddWithValue("@deletedIndex", deletedIndex);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Helper method to read a TaskAttachment from a data reader.
        /// </summary>
        private static TaskAttachment ReadAttachmentFromReader(SQLiteDataReader reader)
        {
            return new TaskAttachment
            {
                Id = reader.GetString(0),
                TaskId = reader.GetString(1),
                ChecklistItemIndex = reader.GetInt32(2),
                FileName = reader.GetString(3),
                StoredFileName = reader.GetString(4),
                MimeType = reader.GetString(5),
                FileSizeBytes = reader.GetInt64(6),
                AddedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetDateTime(8)
            };
        }

        #endregion

        #region Session Lineage

        /// <summary>
        /// Migration: ensure session_lineage and session_messages tables exist.
        /// CreateSchema handles IF NOT EXISTS, but this runs the FTS5 virtual table creation
        /// which needs special error handling for SQLite builds without FTS5.
        /// </summary>
        private void MigrateAddSessionLineage()
        {
            // Attempt to create the FTS5 virtual table and its sync triggers.
            // Wrapped in try/catch because FTS5 is a compile-time SQLite option that may be absent.
            try
            {
                const string fts5Sql = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS session_messages_fts USING fts5(
                        content,
                        content='session_messages',
                        content_rowid='id',
                        tokenize='porter'
                    );

                    -- Trigger: keep FTS index in sync on INSERT
                    CREATE TRIGGER IF NOT EXISTS session_messages_ai
                    AFTER INSERT ON session_messages BEGIN
                        INSERT INTO session_messages_fts(rowid, content)
                        VALUES (new.id, new.content);
                    END;

                    -- Trigger: keep FTS index in sync on DELETE
                    CREATE TRIGGER IF NOT EXISTS session_messages_ad
                    AFTER DELETE ON session_messages BEGIN
                        INSERT INTO session_messages_fts(session_messages_fts, rowid, content)
                        VALUES ('delete', old.id, old.content);
                    END;

                    -- Trigger: keep FTS index in sync on UPDATE
                    CREATE TRIGGER IF NOT EXISTS session_messages_au
                    AFTER UPDATE ON session_messages BEGIN
                        INSERT INTO session_messages_fts(session_messages_fts, rowid, content)
                        VALUES ('delete', old.id, old.content);
                        INSERT INTO session_messages_fts(rowid, content)
                        VALUES (new.id, new.content);
                    END;
                ";
                using var cmd = new SQLiteCommand(fts5Sql, _connection);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskDatabase] FTS5 not available, session message search will use LIKE fallback: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks whether FTS5 is available in this SQLite build by querying the virtual table.
        /// </summary>
        private bool CheckFts5Available()
        {
            try
            {
                const string sql = "SELECT name FROM sqlite_master WHERE type='table' AND name='session_messages_fts'";
                using var cmd = new SQLiteCommand(sql, _connection);
                var result = cmd.ExecuteScalar();
                return result != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Saves a session lineage record (upsert by session_id).
        /// </summary>
        public void SaveSessionLineage(SessionLineageRecord record)
        {
            const string sql = @"
                INSERT INTO session_lineage
                    (session_id, parent_session_id, task_id, agent_name, session_type, summary, session_file_path, started_at, ended_at, created_at)
                VALUES
                    (@sessionId, @parentSessionId, @taskId, @agentName, @sessionType, @summary, @sessionFilePath, @startedAt, @endedAt, @createdAt)
                ON CONFLICT(session_id) DO UPDATE SET
                    parent_session_id = @parentSessionId,
                    task_id           = COALESCE(@taskId, task_id),
                    agent_name        = @agentName,
                    session_type      = @sessionType,
                    summary           = COALESCE(@summary, summary),
                    session_file_path = COALESCE(@sessionFilePath, session_file_path),
                    started_at        = COALESCE(@startedAt, started_at),
                    ended_at          = COALESCE(@endedAt, ended_at)
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@sessionId", record.SessionId);
            command.Parameters.AddWithValue("@parentSessionId", (object)record.ParentSessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@taskId", (object)record.TaskId ?? DBNull.Value);
            command.Parameters.AddWithValue("@agentName", record.AgentName);
            command.Parameters.AddWithValue("@sessionType", record.SessionType ?? "terminal");
            command.Parameters.AddWithValue("@summary", (object)record.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("@sessionFilePath", (object)record.SessionFilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("@startedAt", (object)record.StartedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@endedAt", (object)record.EndedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", record.CreatedAt ?? DateTime.UtcNow.ToString("O"));

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns all session IDs that have already been imported into the lineage table.
        /// Used to skip re-importing existing sessions during bulk sync.
        /// </summary>
        public HashSet<string> GetImportedSessionIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string sql = "SELECT session_id FROM session_lineage";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
            return ids;
        }

        /// <summary>
        /// Gets all session lineage records linked to a task, ordered newest first.
        /// </summary>
        public List<SessionLineageRecord> GetSessionsByTask(string taskId)
        {
            var results = new List<SessionLineageRecord>();

            const string sql = @"
                SELECT id, session_id, parent_session_id, task_id, agent_name, session_type,
                       summary, session_file_path, started_at, ended_at, created_at
                FROM session_lineage
                WHERE task_id = @taskId
                ORDER BY created_at DESC
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@taskId", taskId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add(ReadSessionLineageFromReader(reader));
            }

            return results;
        }

        /// <summary>
        /// Walks the session lineage chain starting from the given session_id,
        /// following parent_session_id links upward. Returns the chain ordered from
        /// oldest ancestor to newest descendant.
        /// </summary>
        public List<SessionLineageRecord> GetSessionLineage(string sessionId)
        {
            // Walk parent links using a recursive CTE
            const string sql = @"
                WITH RECURSIVE chain(id, session_id, parent_session_id, task_id, agent_name,
                    session_type, summary, session_file_path, started_at, ended_at, created_at, depth) AS
                (
                    -- Anchor: start from the requested session
                    SELECT id, session_id, parent_session_id, task_id, agent_name,
                           session_type, summary, session_file_path, started_at, ended_at, created_at, 0
                    FROM session_lineage WHERE session_id = @sessionId

                    UNION ALL

                    -- Walk up to parent sessions
                    SELECT sl.id, sl.session_id, sl.parent_session_id, sl.task_id, sl.agent_name,
                           sl.session_type, sl.summary, sl.session_file_path, sl.started_at, sl.ended_at, sl.created_at, chain.depth + 1
                    FROM session_lineage sl
                    JOIN chain ON sl.session_id = chain.parent_session_id
                    WHERE chain.depth < 50  -- guard against circular references
                )
                SELECT id, session_id, parent_session_id, task_id, agent_name,
                       session_type, summary, session_file_path, started_at, ended_at, created_at
                FROM chain
                ORDER BY depth DESC  -- oldest ancestor first
            ";

            var results = new List<SessionLineageRecord>();

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                results.Add(ReadSessionLineageFromReader(reader));
            }

            return results;
        }

        /// <summary>
        /// Bulk-inserts session messages for a session. Existing messages for this
        /// session are deleted first (replace-all semantics). Runs in a transaction.
        /// </summary>
        public void SaveSessionMessages(string sessionId, List<SessionMessageRecord> messages)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                // Clear existing messages for this session (e.g. re-import)
                const string deleteSql = "DELETE FROM session_messages WHERE session_id = @sessionId";
                using (var delCmd = new SQLiteCommand(deleteSql, _connection, transaction))
                {
                    delCmd.Parameters.AddWithValue("@sessionId", sessionId);
                    delCmd.ExecuteNonQuery();
                }

                const string insertSql = @"
                    INSERT INTO session_messages
                        (session_id, task_id, agent_name, message_index, role, content, tool_name, timestamp)
                    VALUES
                        (@sessionId, @taskId, @agentName, @messageIndex, @role, @content, @toolName, @timestamp)
                ";

                foreach (var msg in messages)
                {
                    using var cmd = new SQLiteCommand(insertSql, _connection, transaction);
                    cmd.Parameters.AddWithValue("@sessionId", sessionId);
                    cmd.Parameters.AddWithValue("@taskId", (object)msg.TaskId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@agentName", (object)msg.AgentName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@messageIndex", msg.MessageIndex);
                    cmd.Parameters.AddWithValue("@role", msg.Role);
                    cmd.Parameters.AddWithValue("@content", (object)msg.Content ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@toolName", (object)msg.ToolName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@timestamp", (object)msg.Timestamp ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Returns session messages without full-text search, supporting optional filters.
        /// Used when no query text is provided — returns all matching messages.
        /// </summary>
        public List<SessionMessageRecord> GetSessionMessages(
            string taskId = null,
            string role = null,
            string agentName = null,
            int limit = 100)
        {
            var results = new List<SessionMessageRecord>();

            var sql = @"
                SELECT id, session_id, task_id, agent_name, message_index,
                       role, content, tool_name, timestamp
                FROM session_messages
                WHERE 1=1
            ";

            if (taskId != null) sql += " AND task_id = @taskId";
            if (role != null) sql += " AND role = @role";
            if (agentName != null) sql += " AND agent_name = @agentName";
            sql += " ORDER BY session_id, message_index LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            if (taskId != null) cmd.Parameters.AddWithValue("@taskId", taskId);
            if (role != null) cmd.Parameters.AddWithValue("@role", role);
            if (agentName != null) cmd.Parameters.AddWithValue("@agentName", agentName);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSessionMessageFromReader(reader));
            }

            return results;
        }

        /// <summary>
        /// Searches session messages for the given query text. Uses FTS5 if available,
        /// falls back to LIKE queries. Supports optional filters for sessionId, taskId,
        /// role, and agentName. Returns matching messages ordered by session, then message_index.
        /// </summary>
        public List<SessionMessageRecord> SearchSessionMessages(
            string query,
            string sessionId = null,
            string taskId = null,
            string role = null,
            string agentName = null,
            int limit = 100)
        {
            // Guard: clamp query length to prevent oversized FTS5 expressions
            if (query != null && query.Length > 500)
                query = query.Substring(0, 500);

            if (_fts5Available)
            {
                // FTS5 path with fallback: malformed FTS5 syntax (e.g. bare "AND", "*", empty)
                // causes a SQLiteException. Catch and retry via the LIKE path.
                try
                {
                    return ExecuteFts5Search(query, sessionId, taskId, role, agentName, limit);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TaskDatabase] FTS5 query failed, falling back to LIKE: {ex.Message}");
                }
            }

            return ExecuteLikeSearch(query, sessionId, taskId, role, agentName, limit);
        }

        private List<SessionMessageRecord> ExecuteFts5Search(
            string query, string sessionId, string taskId, string role, string agentName, int limit)
        {
            var results = new List<SessionMessageRecord>();

            var sql = @"
                SELECT sm.id, sm.session_id, sm.task_id, sm.agent_name, sm.message_index,
                       sm.role, sm.content, sm.tool_name, sm.timestamp
                FROM session_messages sm
                JOIN session_messages_fts fts ON sm.id = fts.rowid
                WHERE session_messages_fts MATCH @query
            ";

            if (sessionId != null) sql += " AND sm.session_id = @sessionId";
            if (taskId != null) sql += " AND sm.task_id = @taskId";
            if (role != null) sql += " AND sm.role = @role";
            if (agentName != null) sql += " AND sm.agent_name = @agentName";
            sql += " ORDER BY sm.session_id, sm.message_index LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@query", query);
            if (sessionId != null) cmd.Parameters.AddWithValue("@sessionId", sessionId);
            if (taskId != null) cmd.Parameters.AddWithValue("@taskId", taskId);
            if (role != null) cmd.Parameters.AddWithValue("@role", role);
            if (agentName != null) cmd.Parameters.AddWithValue("@agentName", agentName);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSessionMessageFromReader(reader));
            }

            return results;
        }

        private List<SessionMessageRecord> ExecuteLikeSearch(
            string query, string sessionId, string taskId, string role, string agentName, int limit)
        {
            var results = new List<SessionMessageRecord>();

            var sql = @"
                SELECT id, session_id, task_id, agent_name, message_index,
                       role, content, tool_name, timestamp
                FROM session_messages
                WHERE content LIKE @query
            ";

            if (sessionId != null) sql += " AND session_id = @sessionId";
            if (taskId != null) sql += " AND task_id = @taskId";
            if (role != null) sql += " AND role = @role";
            if (agentName != null) sql += " AND agent_name = @agentName";
            sql += " ORDER BY session_id, message_index LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@query", $"%{query}%");
            if (sessionId != null) cmd.Parameters.AddWithValue("@sessionId", sessionId);
            if (taskId != null) cmd.Parameters.AddWithValue("@taskId", taskId);
            if (role != null) cmd.Parameters.AddWithValue("@role", role);
            if (agentName != null) cmd.Parameters.AddWithValue("@agentName", agentName);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSessionMessageFromReader(reader));
            }

            return results;
        }

        /// <summary>
        /// Returns the most recent session lineage record whose session_file_path starts with
        /// the given folder prefix. Ordered by ended_at DESC, then created_at DESC.
        /// Returns null if no matching record is found.
        /// </summary>
        public SessionLineageRecord GetMostRecentSessionByFolder(string claudeProjectFolder)
        {
            // Normalize: ensure trailing separator so we don't match sibling folders sharing a prefix
            string folderPrefix = claudeProjectFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            const string sql = @"
                SELECT id, session_id, parent_session_id, task_id, agent_name, session_type,
                       summary, session_file_path, started_at, ended_at, created_at
                FROM session_lineage
                WHERE session_file_path LIKE @folder || '%'
                ORDER BY ended_at DESC, created_at DESC
                LIMIT 1
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@folder", folderPrefix);
            using var reader = command.ExecuteReader();

            return reader.Read() ? ReadSessionLineageFromReader(reader) : null;
        }

        /// <summary>
        /// Updates the summary field for a session lineage record.
        /// Returns the number of rows affected (0 if sessionId not found).
        /// </summary>
        public int UpdateSessionSummary(string sessionId, string summary)
        {
            const string sql = "UPDATE session_lineage SET summary = @summary WHERE session_id = @sessionId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@summary", (object)summary ?? DBNull.Value);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns the last N messages for a session, filtered to the given role, ordered
        /// by message_index DESC (most recent first). Useful for generating session summaries.
        /// </summary>
        public List<SessionMessageRecord> GetRecentSessionMessages(string sessionId, string role, int limit)
        {
            var results = new List<SessionMessageRecord>();

            const string sql = @"
                SELECT id, session_id, task_id, agent_name, message_index,
                       role, content, tool_name, timestamp
                FROM session_messages
                WHERE session_id = @sessionId AND role = @role
                ORDER BY message_index DESC
                LIMIT @limit
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            command.Parameters.AddWithValue("@role", role);
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSessionMessageFromReader(reader));
            }

            return results;
        }

        /// <summary>
        /// Deletes all session messages for a given session_id.
        /// </summary>
        public void DeleteSessionMessages(string sessionId)
        {
            const string sql = "DELETE FROM session_messages WHERE session_id = @sessionId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns true if FTS5 full-text search is available in this SQLite build.
        /// </summary>
        public bool IsFts5Available => _fts5Available;

        private static SessionLineageRecord ReadSessionLineageFromReader(SQLiteDataReader reader)
        {
            return new SessionLineageRecord
            {
                DbId = reader.GetInt32(0),
                SessionId = reader.GetString(1),
                ParentSessionId = reader.IsDBNull(2) ? null : reader.GetString(2),
                TaskId = reader.IsDBNull(3) ? null : reader.GetString(3),
                AgentName = reader.GetString(4),
                SessionType = reader.GetString(5),
                Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
                SessionFilePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                StartedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                EndedAt = reader.IsDBNull(9) ? null : reader.GetString(9),
                CreatedAt = reader.IsDBNull(10) ? null : reader.GetString(10)
            };
        }

        private static SessionMessageRecord ReadSessionMessageFromReader(SQLiteDataReader reader)
        {
            return new SessionMessageRecord
            {
                DbId = reader.GetInt32(0),
                SessionId = reader.GetString(1),
                TaskId = reader.IsDBNull(2) ? null : reader.GetString(2),
                AgentName = reader.IsDBNull(3) ? null : reader.GetString(3),
                MessageIndex = reader.GetInt32(4),
                Role = reader.GetString(5),
                Content = reader.IsDBNull(6) ? null : reader.GetString(6),
                ToolName = reader.IsDBNull(7) ? null : reader.GetString(7),
                Timestamp = reader.IsDBNull(8) ? null : reader.GetString(8)
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
}
