using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using MultiTerminal.MCPServer.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// SQLite database service for persisting Kanban tasks.
    /// Database is stored at %APPDATA%\multiterminal\multiterminal.db
    /// </summary>
    public class TaskDatabase : IDisposable
    {
        private readonly string _databasePath;
        private SQLiteConnection _connection;

        // THE gate for the single shared SQLiteConnection.
        //
        // ONE GATE, ONE IDIOM: every runtime access to _connection MUST begin its method
        // with `using var gate = LockConn();` (the scope-guard below) as the FIRST
        // statement — that Monitor.Enter/Exit on this lock serializes the whole
        // command+reader+transaction lifecycle (a live SQLiteDataReader keeps the
        // connection busy, so the lock must span iteration, not just command creation).
        // TaskDatabase serves concurrent callers — REST controllers on thread-pool
        // threads, the janitor timer, session-import Task.Run threads, and HUD
        // FileSystemWatcher-debounced refreshes — over ONE connection handle, and
        // ADO.NET's SQLiteConnection is not safe for concurrent commands (overlapping
        // readers corrupt reader state).
        //
        // WHY Monitor, NOT SemaphoreSlim: Monitor is REENTRANT, so a locked method that
        // nests into another locked method (or a transaction method that calls a helper)
        // can't self-deadlock; the whole API is synchronous so no async path needs
        // SemaphoreSlim. If TaskDatabase is ever async-ified, do NOT blindly swap in
        // SemaphoreSlim without re-auditing the nesting call graph first.
        //
        // DEADLOCK-SAFE: TaskDatabase is a leaf — it never raises broker events or invokes
        // callbacks while holding the lock (audited: task ad08caac, item 0). Keep it that
        // way: capture data under the lock and raise any event AFTER LockConn() disposes.
        //
        // EXEMPT from the gate (the ONLY methods allowed to touch _connection directly):
        // InitializeDatabase, CreateSchema, the Migrate* chain, and Dispose — all run
        // single-threaded at construction/teardown before the connection is shared. The
        // item-5 verification enforces exactly this exemption by NAME PATTERN
        // (Migrate*|CreateSchema|InitializeDatabase|Dispose) — not by an in-code sentinel —
        // so a new method cannot quietly opt itself out of the gate.
        private readonly object _dbLock = new object();
        private bool _isDisposed;

        // FTS5 availability flag — checked once at init, falls back to LIKE queries if unavailable
        private bool _fts5Available;

        /// <summary>
        /// THE one idiom for touching the shared <see cref="_connection"/> at runtime.
        /// Enters the <see cref="_dbLock"/> Monitor and returns a scope-guard whose
        /// disposal exits it. Use it as the FIRST statement of every method that touches
        /// <see cref="_connection"/>:
        /// <code>using var gate = LockConn();</code>
        /// This serializes the whole command+reader+transaction lifecycle over the single
        /// connection handle (a live SQLiteDataReader keeps the connection busy, so the
        /// lock must span iteration, not just command creation).
        ///
        /// <para><b>FOOTGUN — always `using`:</b> a bare <c>LockConn();</c> (no
        /// <c>using</c>) enters the Monitor and discards the handle, so <c>Monitor.Exit</c>
        /// NEVER runs — a silent, permanent lock hold that hangs every other DB caller.
        /// Always write <c>using var gate = LockConn();</c>. The item-5 verification
        /// matches that full <c>using var … = LockConn()</c> pattern, so a missing-`using`
        /// misuse FAILS the check rather than shipping.</para>
        ///
        /// <para><b>Never in an iterator or async method:</b> a <c>using var</c> in a
        /// <c>yield return</c> iterator or <c>async</c> method acquires late / holds across
        /// suspension. TaskDatabase's API is fully synchronous with no iterators (item-5
        /// verification asserts this stays true); keep it that way.</para>
        ///
        /// <para><b>Why Monitor, not SemaphoreSlim:</b> Monitor is reentrant on the same
        /// thread, so a locked method that nests into another locked method (or a
        /// transaction method that calls a helper) can't self-deadlock; a
        /// SemaphoreSlim(1,1) would self-deadlock on the second acquire. The whole API is
        /// synchronous, so no async path needs SemaphoreSlim. If TaskDatabase is ever
        /// async-ified, do NOT blindly swap in SemaphoreSlim without re-auditing the
        /// nesting call graph first.</para>
        ///
        /// <para><b>Deadlock-safety:</b> the guarded body must NOT raise MessageBroker
        /// events or invoke external callbacks while the lock is held (the broker has its
        /// own locks → lock-ordering hazard). TaskDatabase is a leaf and does none of this
        /// today (audited: task ad08caac). If a future method needs to fire an event,
        /// capture the data under the lock and raise the event AFTER the guard disposes.</para>
        /// </summary>
        private LockHandle LockConn()
        {
            Monitor.Enter(_dbLock);
            return new LockHandle(_dbLock);
        }

        /// <summary>
        /// Scope-guard returned by <see cref="LockConn"/>. A readonly struct so
        /// <c>using var gate = LockConn();</c> disposes without boxing; disposal exits the
        /// Monitor entered by <see cref="LockConn"/>. One Enter ↔ one Dispose, so nested
        /// (reentrant) LockConn calls stay balanced.
        /// </summary>
        private readonly struct LockHandle : IDisposable
        {
            private readonly object _gate;

            public LockHandle(object gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                Monitor.Exit(_gate);
            }
        }

        /// <summary>
        /// Gets the path to the tasks database.
        /// </summary>
        public static string GetDatabasePath()
        {
            var testDb = Environment.GetEnvironmentVariable("MULTITERMINAL_TEST_DB");
            if (!string.IsNullOrEmpty(testDb)) return testDb;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "multiterminal");
            return Path.Combine(folder, "multiterminal.db");
        }

        /// <summary>
        /// Creates a new TaskDatabase instance.
        /// </summary>
        public TaskDatabase()
        {
            // Delegate to GetDatabasePath so the MULTITERMINAL_TEST_DB override is
            // honored here too (tests construct an isolated temp DB the same way
            // PlanDatabaseTests does). Production-identical when the env var is
            // unset: GetDatabasePath returns the same %APPDATA%/multiterminal path.
            _databasePath = GetDatabasePath();
            string folder = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            // Open through the one factory (bb2b0104 condition 2) so WAL / pooling / busy_timeout
            // can't drift from what every other connection owner uses. TaskDatabase owns this
            // connection and serializes its own access via LockConn() (ad08caac); no other class
            // touches this handle (the Connection escape hatch was deleted in bb2b0104).
            _connection = MultiterminalDb.Open();

            // Always run CreateSchema - all statements use IF NOT EXISTS so it's idempotent
            CreateSchema();

            // Run migrations for schema changes, each recorded in schema_migrations so already-applied
            // migrations are skipped on subsequent startups (P5 / 1df2a534). Every MigrateXxx method is
            // idempotent (PRAGMA / sqlite_master check before ALTER), so the first startup after this
            // ledger was introduced re-runs and records all of them with no data risk; later startups
            // skip the recorded ones. The nameof() key is the method name, stable across renames-with-refactor.
            RunMigration(nameof(MigrateAddProjectIdToTasks), MigrateAddProjectIdToTasks);
            RunMigration(nameof(MigrateAddTaskSummaries), MigrateAddTaskSummaries);
            RunMigration(nameof(MigrateAddTaskStackColumns), MigrateAddTaskStackColumns);
            RunMigration(nameof(MigrateAddStaleColumns), MigrateAddStaleColumns);
            RunMigration(nameof(MigrateAddPriorityColumn), MigrateAddPriorityColumn);
            RunMigration(nameof(MigrateAddCriticalSection), MigrateAddCriticalSection);
            RunMigration(nameof(MigrateAddChecklistToTasks), MigrateAddChecklistToTasks);
            RunMigration(nameof(MigrateAddImplementationChecklistToTasks), MigrateAddImplementationChecklistToTasks);
            RunMigration(nameof(MigrateAddPendingEnhancement), MigrateAddPendingEnhancement);
            RunMigration(nameof(MigrateAddTeamMemberProfiles), MigrateAddTeamMemberProfiles);
            RunMigration(nameof(MigrateAddHelperSessions), MigrateAddHelperSessions);
            RunMigration(nameof(MigrateAddTaskDocumentation), MigrateAddTaskDocumentation);
            RunMigration(nameof(MigrateCleanupProfileNullStrings), MigrateCleanupProfileNullStrings);
            RunMigration(nameof(MigrateAddOnlineStatusToProfiles), MigrateAddOnlineStatusToProfiles);
            RunMigration(nameof(MigrateAddContinuationNotesToTasks), MigrateAddContinuationNotesToTasks);
            RunMigration(nameof(MigrateAddAutoStatusToTasks), MigrateAddAutoStatusToTasks);
            RunMigration(nameof(MigrateAddReviewNotesToTasks), MigrateAddReviewNotesToTasks);
            RunMigration(nameof(MigrateAddIsQuickTaskToTasks), MigrateAddIsQuickTaskToTasks);
            RunMigration(nameof(MigrateAddSortOrderToTasks), MigrateAddSortOrderToTasks);
            RunMigration(nameof(MigrateAddUserInbox), MigrateAddUserInbox);
            RunMigration(nameof(MigrateAddProjectIdsToProfiles), MigrateAddProjectIdsToProfiles);
            RunMigration(nameof(MigrateAddTaskAttachments), MigrateAddTaskAttachments);
            RunMigration(nameof(MigrateAddAgentFieldsToProfiles), MigrateAddAgentFieldsToProfiles);
            RunMigration(nameof(MigrateAddTeamLeadToProfiles), MigrateAddTeamLeadToProfiles);
            RunMigration(nameof(MigrateAddSessionLineage), MigrateAddSessionLineage);
            RunMigration(nameof(MigrateAddKnowledgeBase), MigrateAddKnowledgeBase);
            RunMigration(nameof(MigrateAddTaskRelationships), MigrateAddTaskRelationships);
            RunMigration(nameof(MigrateAddTaskFileLinks), MigrateAddTaskFileLinks);
            RunMigration(nameof(MigrateAddChecklistItemIndexToFileLinks), MigrateAddChecklistItemIndexToFileLinks);
            RunMigration(nameof(MigrateAddOwnerProfile), MigrateAddOwnerProfile);
            RunMigration(nameof(MigrateAddSourceControlAccounts), MigrateAddSourceControlAccounts);
            RunMigration(nameof(MigrateAddAgentInvocations), MigrateAddAgentInvocations);
            RunMigration(nameof(MigrateAddTaskReports), MigrateAddTaskReports);
            RunMigration(nameof(MigrateAddNotificationEvents), MigrateAddNotificationEvents);
            RunMigration(nameof(MigrateAddMessageImages), MigrateAddMessageImages);
            RunMigration(nameof(MigrateAddKnowledgeQueryHash), MigrateAddKnowledgeQueryHash);
            RunMigration(nameof(MigrateAddKnowledgeAttentionDecay), MigrateAddKnowledgeAttentionDecay);
            RunMigration(nameof(MigrateAddSessionLifecycleStatus), MigrateAddSessionLifecycleStatus);
            RunMigration(nameof(MigrateAddTaskWorktrees), MigrateAddTaskWorktrees);
            RunMigration(nameof(MigrateAddBranchMetadata), MigrateAddBranchMetadata);
            RunMigration(nameof(MigrateAddAgentToTaskWorktrees), MigrateAddAgentToTaskWorktrees);
            RunMigration(nameof(MigrateNormalizeNoteTabPaths), MigrateNormalizeNoteTabPaths);

            // Seed default agent profiles on first run
            SeedDefaultProfiles();

            // Check FTS5 support after all tables are created
            _fts5Available = CheckFts5Available();
        }

        /// <summary>
        /// Migration runner (P5 / 1df2a534): runs <paramref name="migration"/> only if it has not been
        /// recorded in schema_migrations, then records it. Every caller is in InitializeDatabase, which
        /// runs single-threaded before the connection is shared — so, like CreateSchema and the Migrate*
        /// methods, these three helpers are exempt from LockConn (init-phase, name-pattern allowlisted).
        /// </summary>
        private void RunMigration(string name, Action migration)
        {
            if (IsMigrationApplied(name))
            {
                return;
            }

            migration();
            RecordMigration(name);
        }

        private bool IsMigrationApplied(string name)
        {
            using var command = new SQLiteCommand("SELECT 1 FROM schema_migrations WHERE name = @name LIMIT 1", _connection);
            command.Parameters.AddWithValue("@name", name);
            return command.ExecuteScalar() != null;
        }

        private void RecordMigration(string name)
        {
            using var command = new SQLiteCommand("INSERT OR IGNORE INTO schema_migrations (name, applied_at) VALUES (@name, @applied_at)", _connection);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@applied_at", DateTime.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
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

                -- Schema migration ledger (P5 / 1df2a534): one row per applied MigrateXxx, so the
                -- migration runner can skip already-applied migrations and there's an audit trail of
                -- when each landed. Idempotent migrations still run once on the first upgrade that
                -- introduces this table (schema_migrations starts empty), recording as they go.
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    name TEXT PRIMARY KEY,
                    applied_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

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

                -- Projects table for grouping tasks.
                -- NOTE: ProjectDatabase is the real owner of this table (full rich schema); this
                -- minimal CREATE only exists so TaskDatabase's FKs resolve. Because TaskDatabase is
                -- constructed BEFORE ProjectDatabase, this statement wins the IF-NOT-EXISTS race on a
                -- clean install, so its column set must stay a strict subset that includes every
                -- column any pre-ProjectDatabase-migration read needs. 'path' is included here so the
                -- two CREATEs agree; ProjectDatabase's migration also ALTER-adds it defensively. (df1f521f)
                CREATE TABLE IF NOT EXISTS projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    path TEXT,
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

                -- Session agent map: written by session-status-hook to record which agent owns each session
                CREATE TABLE IF NOT EXISTS session_agent_map (
                    session_id TEXT PRIMARY KEY,
                    agent_name TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    started_at TEXT NOT NULL DEFAULT (datetime('now')),
                    ended_at TEXT
                );

                -- Knowledge entries: institutional memory facts, decisions, and patterns
                CREATE TABLE IF NOT EXISTS knowledge_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT,
                    category TEXT NOT NULL,
                    title TEXT NOT NULL,
                    content TEXT NOT NULL,
                    source_type TEXT NOT NULL DEFAULT 'manual',
                    source_id TEXT,
                    source_agent TEXT,
                    tags TEXT,
                    confidence TEXT NOT NULL DEFAULT 'confirmed',
                    superseded_by INTEGER,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE INDEX IF NOT EXISTS idx_knowledge_project ON knowledge_entries(project_id);
                CREATE INDEX IF NOT EXISTS idx_knowledge_category ON knowledge_entries(category);
                CREATE INDEX IF NOT EXISTS idx_knowledge_confidence ON knowledge_entries(confidence);

                -- Code digests: per-file summaries for fast agent orientation
                CREATE TABLE IF NOT EXISTS code_digests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT,
                    file_path TEXT NOT NULL,
                    file_hash TEXT NOT NULL,
                    purpose TEXT,
                    key_classes TEXT,
                    key_methods TEXT,
                    patterns TEXT,
                    gotchas TEXT,
                    dependencies TEXT,
                    line_count INTEGER,
                    digest_model TEXT DEFAULT 'haiku',
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(project_id, file_path)
                );

                CREATE INDEX IF NOT EXISTS idx_digests_project ON code_digests(project_id);
                CREATE INDEX IF NOT EXISTS idx_digests_hash ON code_digests(file_hash);

                -- Chat messages: inter-terminal MCP communication history
                CREATE TABLE IF NOT EXISTS chat_messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id TEXT UNIQUE NOT NULL,
                    from_terminal TEXT NOT NULL,
                    to_terminal TEXT NOT NULL,
                    content TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    is_broadcast INTEGER DEFAULT 0,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP
                );

                CREATE INDEX IF NOT EXISTS idx_chat_messages_timestamp ON chat_messages(timestamp);
                CREATE INDEX IF NOT EXISTS idx_chat_messages_terminals ON chat_messages(from_terminal, to_terminal);

                CREATE TABLE IF NOT EXISTS task_relationships (
                    id TEXT PRIMARY KEY,
                    source_task_id TEXT NOT NULL,
                    target_task_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    created_by TEXT,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(source_task_id, target_task_id, type)
                );
                CREATE INDEX IF NOT EXISTS idx_task_rel_source ON task_relationships(source_task_id);
                CREATE INDEX IF NOT EXISTS idx_task_rel_target ON task_relationships(target_task_id);

                CREATE TABLE IF NOT EXISTS task_file_links (
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    description TEXT,
                    line_start INTEGER,
                    line_end INTEGER,
                    added_by TEXT,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(task_id, file_path)
                );
                CREATE INDEX IF NOT EXISTS idx_task_files_task ON task_file_links(task_id);

                CREATE TABLE IF NOT EXISTS task_worktrees (
                    task_id TEXT NOT NULL,
                    worktree_path TEXT NOT NULL,
                    branch_name TEXT NOT NULL,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    status TEXT NOT NULL DEFAULT 'active',
                    agent_name TEXT NOT NULL DEFAULT '__legacy__',
                    is_canonical INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (task_id, agent_name)
                );
                CREATE INDEX IF NOT EXISTS idx_task_worktrees_status ON task_worktrees(status);
                CREATE INDEX IF NOT EXISTS idx_task_worktrees_task ON task_worktrees(task_id);

                CREATE TABLE IF NOT EXISTS project_notes (
                    id TEXT PRIMARY KEY,
                    project_path TEXT NOT NULL,
                    content TEXT DEFAULT '',
                    updated_at DATETIME DEFAULT (datetime('now')),
                    updated_by TEXT,
                    UNIQUE(project_path)
                );

                CREATE TABLE IF NOT EXISTS project_note_tabs (
                    id TEXT PRIMARY KEY,
                    project_path TEXT NOT NULL,
                    tab_name TEXT NOT NULL,
                    content TEXT DEFAULT '',
                    tab_order INTEGER NOT NULL DEFAULT 0,
                    is_default INTEGER NOT NULL DEFAULT 0,
                    created_at DATETIME DEFAULT (datetime('now')),
                    updated_at DATETIME DEFAULT (datetime('now')),
                    UNIQUE(project_path, tab_name)
                );
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
        /// Migration: Add review_notes column for inline code review comments from the diff viewer.
        /// </summary>
        private void MigrateAddReviewNotesToTasks()
        {
            bool hasReviewNotes = false;
            using (var pragmaCmd = new SQLiteCommand("PRAGMA table_info(tasks)", _connection))
            using (var reader = pragmaCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("review_notes", StringComparison.OrdinalIgnoreCase))
                    {
                        hasReviewNotes = true;
                        break;
                    }
                }
            }

            if (!hasReviewNotes)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN review_notes TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration: Add is_quick_task column. Marks lightweight attribution-anchor
        /// tasks (task d42423e3) that bypass the kanban lifecycle. Default 0 (false)
        /// preserves all existing rows as regular tasks.
        /// </summary>
        private void MigrateAddIsQuickTaskToTasks()
        {
            bool hasIsQuickTask = false;
            using (var pragmaCmd = new SQLiteCommand("PRAGMA table_info(tasks)", _connection))
            using (var reader = pragmaCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("is_quick_task", StringComparison.OrdinalIgnoreCase))
                    {
                        hasIsQuickTask = true;
                        break;
                    }
                }
            }

            if (!hasIsQuickTask)
            {
                const string sql = "ALTER TABLE tasks ADD COLUMN is_quick_task INTEGER DEFAULT 0";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Adds the sort_order column to tasks and backfills existing rows so today's
        /// creation-time order is preserved. Gap-based (1000-unit increments) so that
        /// midpoint insertion on drag-rank rarely needs to renumber. Composite index
        /// (status, sort_order) backs the kanban list query's ORDER BY.
        /// </summary>
        private void MigrateAddSortOrderToTasks()
        {
            bool hasSortOrder = false;
            using (var pragmaCmd = new SQLiteCommand("PRAGMA table_info(tasks)", _connection))
            using (var reader = pragmaCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("sort_order", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSortOrder = true;
                        break;
                    }
                }
            }

            if (!hasSortOrder)
            {
                using var alter = new SQLiteCommand("ALTER TABLE tasks ADD COLUMN sort_order REAL", _connection);
                alter.ExecuteNonQuery();

                // Backfill: assign 1000, 2000, 3000... per status bucket in created_at ASC
                // order. ROW_NUMBER() requires SQLite 3.25+, which our SQLite assembly
                // ships. Project_id is intentionally ignored for partitioning — relative
                // order within (project, status) is identical to within (status) when the
                // source order is created_at.
                const string backfill = @"
                    WITH ordered AS (
                        SELECT id, ROW_NUMBER() OVER (PARTITION BY status ORDER BY created_at ASC) AS rn
                        FROM tasks
                    )
                    UPDATE tasks
                    SET sort_order = (SELECT rn * 1000.0 FROM ordered WHERE ordered.id = tasks.id)
                    WHERE sort_order IS NULL
                ";
                using var backfillCmd = new SQLiteCommand(backfill, _connection);
                backfillCmd.ExecuteNonQuery();

                using var index = new SQLiteCommand(
                    "CREATE INDEX IF NOT EXISTS idx_tasks_status_sort ON tasks(status, sort_order)",
                    _connection);
                index.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Load all tasks from the database, including helpers.
        /// </summary>
        public List<KanbanTask> LoadAllTasks()
        {
            using var gate = LockConn();
            var tasks = new List<KanbanTask>();

            // ORDER BY: sort_order is the manual rank (lower first) within a status
            // column. NULLS LAST (encoded via CASE for portability — `NULLS LAST` is
            // only valid SQLite 3.30+) keeps any unseeded rows at the bottom of their
            // column rather than first. Within equal sort_order (or both null), fall
            // back to created_at ASC so the order is deterministic.
            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
                FROM tasks
                ORDER BY CASE WHEN sort_order IS NULL THEN 1 ELSE 0 END,
                         sort_order ASC,
                         created_at ASC
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
        /// Expects columns: id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
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
                AutoStatus = !reader.IsDBNull(22) && reader.GetInt32(22) != 0,
                ReviewNotes = reader.IsDBNull(23) ? null : reader.GetString(23),
                IsQuickTask = !reader.IsDBNull(24) && reader.GetInt32(24) != 0,
                SortOrder = reader.IsDBNull(25) ? (double?)null : reader.GetDouble(25)
            };
        }

        /// <summary>
        /// Save a task (insert or update).
        /// </summary>
        public void SaveTask(KanbanTask task)
        {
            using var gate = LockConn();
            // sort_order in the UPDATE clause uses COALESCE so that a SaveTask call
            // with task.SortOrder=null does NOT overwrite an existing non-null DB
            // value. UpdateSortOrder() is the single source of truth for changing
            // the rank; SaveTask round-trips it when the caller knows the value.
            const string sql = @"
                INSERT INTO tasks (id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order)
                VALUES (@id, @title, @description, @status, @assignee, @createdBy, @createdAt, @updatedAt, @projectId, @subStatus, @pausedAt, @flaggedStaleAt, @staleLevel, @staleNotifiedAt, @staleResponse, @priority, @checklistJson, @plan, @implementationSummary, @testResults, @implementationChecklistJson, @continuationNotes, @autoStatus, @reviewNotes, @isQuickTask, @sortOrder)
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
                    review_notes = @reviewNotes,
                    is_quick_task = @isQuickTask,
                    sort_order = COALESCE(@sortOrder, sort_order),
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
            command.Parameters.AddWithValue("@reviewNotes", (object)task.ReviewNotes ?? DBNull.Value);
            command.Parameters.AddWithValue("@isQuickTask", task.IsQuickTask ? 1 : 0);
            command.Parameters.AddWithValue("@sortOrder", (object)task.SortOrder ?? DBNull.Value);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns the next sort_order value to seed for a new task in the given
        /// status column. Tasks are inserted at the END of their column with a
        /// 1000-unit gap from the current max, leaving room for midpoint inserts
        /// on drag-rank without renumbering.
        /// </summary>
        public double GetNextSortOrderForStatus(string status)
        {
            using var gate = LockConn();
            const string sql = "SELECT MAX(sort_order) FROM tasks WHERE status = @status";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@status", status ?? "todo");
            var result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                return 1000.0;
            return Convert.ToDouble(result) + 1000.0;
        }

        /// <summary>
        /// Update the sort_order of a single task. Called by MessageBroker.ReorderTask
        /// after the broker computes the midpoint between drag-rank neighbors.
        /// Returns false if the row does not exist.
        /// </summary>
        public bool UpdateSortOrder(string taskId, double newSortOrder)
        {
            using var gate = LockConn();
            const string sql = @"
                UPDATE tasks
                SET sort_order = @sortOrder,
                    updated_at = @updatedAt
                WHERE id = @id
            ";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", taskId);
            command.Parameters.AddWithValue("@sortOrder", newSortOrder);
            command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
            return command.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Renumber every task in the given status column to 1000-unit gaps,
        /// preserving the current order. Called from MessageBroker.ReorderTask
        /// when the gap between drag-rank neighbors collapses below epsilon
        /// (item 7 — float-collapse guard).
        /// </summary>
        public void RebalanceSortOrder(string status)
        {
            // CTE assigns 1000, 2000, 3000... in the current visible order
            // (sort_order asc, created_at asc — matches LoadAllTasks). The
            // UPDATE then writes the new rank back. Wrapped in a transaction so
            // a partial write can't leave the column half-rebalanced. Whole
            // transaction is serialized on _dbLock (runtime site: MessageBroker.ReorderTask).
            using var gate = LockConn();
            using var tx = _connection.BeginTransaction();
            try
            {
                const string sql = @"
                    WITH ordered AS (
                        SELECT id, ROW_NUMBER() OVER (
                            ORDER BY CASE WHEN sort_order IS NULL THEN 1 ELSE 0 END,
                                     sort_order ASC,
                                     created_at ASC
                        ) AS rn
                        FROM tasks
                        WHERE status = @status
                    )
                    UPDATE tasks
                    SET sort_order = (SELECT rn * 1000.0 FROM ordered WHERE ordered.id = tasks.id)
                    WHERE status = @status
                ";
                using var command = new SQLiteCommand(sql, _connection, tx);
                command.Parameters.AddWithValue("@status", status ?? "todo");
                command.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Atomically pause the given sibling task ids AND activate <paramref name="taskId"/> in ONE
        /// transaction (7c59c004). Either both land or neither: an activation failure rolls the pauses back,
        /// so the tasks table can never hold two rows <c>sub_status='active'</c> for one assignee (the failure
        /// mode 1df2a534 could only mitigate at read time). Returns the ids ACTUALLY paused (those still active
        /// at pause time) so the caller can swap their cache entries and report them. Throws — with the
        /// transaction rolled back, nothing changed — if the target row is missing or no longer
        /// <c>in_progress</c> at activation time.
        /// <para>Pauses BY ID (7c59c004 Codex class-close), NOT by an <c>assignee</c> SQL comparison: the
        /// caller (SetTaskActive) discovers the sibling set with the authoritative C# agent-name equality
        /// (<c>OrdinalIgnoreCase</c>) under the per-assignee lock and passes those ids here. That removes the
        /// C#-vs-SQLite collation dependency entirely — no ASCII/non-ASCII case-fold gap can leave a durable
        /// two-active — and deletes the old surprise-sibling defensive path.</para>
        /// <para>Scope boundary: this transaction spans DB STATE ONLY. Git/worktree side-effects are the
        /// caller's POST-commit, best-effort concern and are deliberately NOT in here — a worktree failure must
        /// never roll back a committed activation.</para>
        /// </summary>
        public List<string> SetTaskActiveTransactional(string taskId, IReadOnlyList<string> siblingIdsToPause, DateTime pausedAt)
        {
            using var gate = LockConn();
            using var tx = _connection.BeginTransaction();
            try
            {
                // 1+2) Pause EXACTLY the caller-discovered siblings, by id. Each pause is guarded on the row
                //      still being active, so a row that changed concurrently is skipped (0 rows) rather than
                //      blindly overwritten; the returned list is the ids actually paused.
                var paused = new List<string>();
                if (siblingIdsToPause != null && siblingIdsToPause.Count > 0)
                {
                    const string pauseSql = @"
                        UPDATE tasks SET sub_status = 'paused', paused_at = @pausedAt, updated_at = @now
                        WHERE id = @id AND status = 'in_progress' AND sub_status = 'active'";
                    foreach (var siblingId in siblingIdsToPause)
                    {
                        if (siblingId == null || string.Equals(siblingId, taskId, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        using var pause = new SQLiteCommand(pauseSql, _connection, tx);
                        pause.Parameters.AddWithValue("@id", siblingId);
                        pause.Parameters.AddWithValue("@pausedAt", pausedAt);
                        pause.Parameters.AddWithValue("@now", DateTime.UtcNow);
                        if (pause.ExecuteNonQuery() > 0)
                        {
                            paused.Add(siblingId);
                        }
                    }
                }

                // 3) Activate the target — still requiring in_progress, so a task that went done/deleted
                //    between the caller's validation and here fails the whole transaction.
                const string activateSql = @"
                    UPDATE tasks SET sub_status = 'active', paused_at = NULL, updated_at = @now
                    WHERE id = @taskId AND status = 'in_progress'";
                using (var activate = new SQLiteCommand(activateSql, _connection, tx))
                {
                    activate.Parameters.AddWithValue("@taskId", taskId);
                    activate.Parameters.AddWithValue("@now", DateTime.UtcNow);
                    if (activate.ExecuteNonQuery() == 0)
                    {
                        throw new InvalidOperationException(
                            $"Task {taskId} not found or not in_progress at activation time.");
                    }
                }

                tx.Commit();
                return paused;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Delete a task by ID.
        /// </summary>
        public void DeleteTask(string taskId)
        {
            using var gate = LockConn();
            // Cascade-delete any relationships and file links involving this task
            DeleteRelationshipsForTask(taskId);
            DeleteFileLinksForTask(taskId);

            const string sql = "DELETE FROM tasks WHERE id = @id";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", taskId);
            command.ExecuteNonQuery();
        }

        #region Task Relationships

        public void AddRelationship(string id, string sourceTaskId, string targetTaskId, string type, string createdBy)
        {
            using var gate = LockConn();
            const string sql = @"
                INSERT OR IGNORE INTO task_relationships (id, source_task_id, target_task_id, type, created_by, created_at)
                VALUES (@id, @sourceTaskId, @targetTaskId, @type, @createdBy, @createdAt)
            ";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@sourceTaskId", sourceTaskId);
            cmd.Parameters.AddWithValue("@targetTaskId", targetTaskId);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@createdBy", createdBy ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        public void RemoveRelationshipsBetween(string taskId1, string taskId2)
        {
            using var gate = LockConn();
            const string sql = @"
                DELETE FROM task_relationships
                WHERE (source_task_id = @id1 AND target_task_id = @id2)
                   OR (source_task_id = @id2 AND target_task_id = @id1)
            ";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id1", taskId1);
            cmd.Parameters.AddWithValue("@id2", taskId2);
            cmd.ExecuteNonQuery();
        }

        public List<MCPServer.Models.TaskRelationship> GetRelationshipsForTask(string taskId)
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT id, source_task_id, target_task_id, type, created_by, created_at
                FROM task_relationships
                WHERE source_task_id = @taskId OR target_task_id = @taskId
                ORDER BY created_at
            ";
            var results = new List<MCPServer.Models.TaskRelationship>();
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new MCPServer.Models.TaskRelationship
                {
                    Id = reader.GetString(0),
                    SourceTaskId = reader.GetString(1),
                    TargetTaskId = reader.GetString(2),
                    Type = reader.GetString(3),
                    CreatedBy = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = DateTime.Parse(reader.GetString(5))
                });
            }
            return results;
        }

        public void DeleteRelationshipsForTask(string taskId)
        {
            using var gate = LockConn();
            const string sql = "DELETE FROM task_relationships WHERE source_task_id = @taskId OR target_task_id = @taskId";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.ExecuteNonQuery();
        }

        #endregion

        #region Task File Links

        public void AddFileLink(string id, string taskId, string filePath, string description, int? lineStart, int? lineEnd, string addedBy, int? checklistItemIndex = null)
        {
            using var gate = LockConn();
            const string sql = @"
                INSERT OR IGNORE INTO task_file_links (id, task_id, file_path, description, line_start, line_end, added_by, checklist_item_index, created_at)
                VALUES (@id, @taskId, @filePath, @description, @lineStart, @lineEnd, @addedBy, @checklistItemIndex, @createdAt)
            ";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@lineStart", lineStart.HasValue ? (object)lineStart.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@lineEnd", lineEnd.HasValue ? (object)lineEnd.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@addedBy", addedBy ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@checklistItemIndex", checklistItemIndex.HasValue ? (object)checklistItemIndex.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        public void RemoveFileLink(string taskId, string filePath)
        {
            using var gate = LockConn();
            const string sql = "DELETE FROM task_file_links WHERE task_id = @taskId AND file_path = @filePath";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.ExecuteNonQuery();
        }

        public List<MCPServer.Models.TaskFileLink> GetFileLinksForTask(string taskId)
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT id, task_id, file_path, description, line_start, line_end, added_by, created_at, checklist_item_index
                FROM task_file_links
                WHERE task_id = @taskId
                ORDER BY created_at
            ";
            var results = new List<MCPServer.Models.TaskFileLink>();
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new MCPServer.Models.TaskFileLink
                {
                    Id = reader.GetString(0),
                    TaskId = reader.GetString(1),
                    FilePath = reader.GetString(2),
                    Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                    LineStart = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                    LineEnd = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
                    AddedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CreatedAt = DateTime.Parse(reader.GetString(7)),
                    ChecklistItemIndex = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8)
                });
            }
            return results;
        }

        // Looks up which checklist items are associated with a file path. Used by
        // per-item review-note routing in HandleCodeReviewVerdict (task 87ee90c3):
        // a comment about file X only bounces items the routing decides "touch" X.
        // Returns:
        //   - non-empty set of item indexes when item-scoped links exist for that file.
        //   - null when ANY task-scoped link (checklist_item_index IS NULL) exists for
        //     the file — caller should treat that as "applies to all items"
        //     (backward-compatible fallback for tasks linked before this column shipped).
        //   - empty set when no link exists at all — caller decides fallback (typically
        //     bounce all testing items so notes aren't lost).
        // Path matching is case-insensitive and tolerates forward/backslash mismatch
        // because reviewer comments and link_task_file callers don't always agree.
        public HashSet<int> GetItemsLinkedToFile(string taskId, string filePath)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(filePath))
                return new HashSet<int>();

            const string sql = @"
                SELECT file_path, checklist_item_index
                FROM task_file_links
                WHERE task_id = @taskId
            ";
            var indexes = new HashSet<int>();
            bool sawTaskScoped = false;
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rowPath = reader.GetString(0);
                if (!FilePathsEquivalent(rowPath, filePath)) continue;

                if (reader.IsDBNull(1))
                {
                    sawTaskScoped = true;
                }
                else
                {
                    indexes.Add(reader.GetInt32(1));
                }
            }
            return sawTaskScoped ? null : indexes;
        }

        // Equivalence test for the routing path-match — same file / case / separator
        // tolerance the HUD Git tab already uses. Reviewers paste paths from the diff
        // viewer (forward-slash); link_task_file callers may pass backslashes.
        private static bool FilePathsEquivalent(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        public void DeleteFileLinksForTask(string taskId)
        {
            using var gate = LockConn();
            const string sql = "DELETE FROM task_file_links WHERE task_id = @taskId";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Query active-task linkage for a batch of file paths. Returns the most
        /// recent active-task association per file (file_path → (TaskId, Title,
        /// Assignee)). Used by the HUD Git tab's Phase 2 attribution overlays
        /// — feeds the `[task-id]` and `[agent]` chips on uncommitted-file rows
        /// and underlies the cross-task contamination banner.
        ///
        /// <para>Only matches tasks where <c>status = 'in_progress'</c> — completed
        /// or paused tasks are excluded so stale linkages don't bleed onto fresh
        /// uncommitted work.</para>
        /// </summary>
        public Dictionary<string, (string TaskId, string Title, string Assignee)> GetActiveTaskLinkageForFiles(
            System.Collections.Generic.IReadOnlyList<string> absolutePaths)
        {
            using var gate = LockConn();
            var result = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
            if (absolutePaths == null || absolutePaths.Count == 0) return result;

            // Runs on Task.Run threads from HudGitRenderer's FileSystemWatcher-debounced
            // refresh; the LockConn() gate above serializes it with all other DB access.
            // Originally a debugger BLOCKER (item [11]): concurrent commands on the shared
            // connection produced undefined reader state.
            var sb = new System.Text.StringBuilder();
            sb.Append("SELECT f.file_path, t.id, t.title, t.assignee, f.created_at ");
            sb.Append("FROM task_file_links f JOIN tasks t ON t.id = f.task_id ");
            sb.Append("WHERE t.status = 'in_progress' AND f.file_path IN (");
            for (int i = 0; i < absolutePaths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("@p").Append(i);
            }
            // Deterministic tie-break via rowid so chip selection doesn't flicker
            // between agents when two links share a created_at second.
            sb.Append(") ORDER BY f.created_at DESC, f.rowid DESC");

            // Only the parameter name suffixes (@p0, @p1, ...) are concatenated;
            // file path VALUES bind via SQLiteParameter below, so no injection.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sb.ToString(), _connection);
#pragma warning restore CA2100
            for (int i = 0; i < absolutePaths.Count; i++)
            {
                cmd.Parameters.AddWithValue("@p" + i, absolutePaths[i] ?? string.Empty);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string filePath = reader.GetString(0);
                // First row per file_path wins (most recent + deterministic
                // tie-break). When the same file is claimed by multiple
                // active tasks, only the primary chip is shown — the
                // contamination banner uses GetDistinctActiveTaskIdsForFiles
                // (separate query) to detect the multi-claim case, since
                // this dedup would otherwise hide it.
                if (result.ContainsKey(filePath)) continue;
                string taskId = reader.GetString(1);
                string title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                string assignee = reader.IsDBNull(3) ? null : reader.GetString(3);
                result[filePath] = (taskId, title, assignee);
            }
            return result;
        }

        /// <summary>
        /// Query completed-task linkage for a batch of file paths. Returns the
        /// most recent <c>done</c>-task association per file. Used as the
        /// fallback layer for the HUD Git tab's per-file chips: when an
        /// uncommitted file has no active-task claim but WAS linked to a task
        /// that has since shipped, the chip surfaces the shipped task in a
        /// muted/greyed state ("done — commit when ready").
        ///
        /// <para>Mirrors <see cref="GetActiveTaskLinkageForFiles"/> with two
        /// divergences: the status filter (<c>'done'</c> vs <c>'in_progress'</c>)
        /// and a 3-day recency cutoff on <c>tasks.updated_at</c> — the active
        /// path has no recency cutoff because in-progress tasks are by
        /// definition still in flight. See body comment for the recency
        /// rationale and its phantom-resurrection caveat.
        /// Same lock, same parameterized IN-list, same deterministic tie-break.
        /// Contamination logic does NOT use this — completed tasks are
        /// intentionally excluded from cross-task contamination since shipped
        /// work landing before its files commit is a normal in-flight state,
        /// not a banner-worthy collision.</para>
        /// </summary>
        public Dictionary<string, (string TaskId, string Title, string Assignee)> GetCompletedTaskLinkageForFiles(
            System.Collections.Generic.IReadOnlyList<string> absolutePaths)
        {
            using var gate = LockConn();
            var result = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
            if (absolutePaths == null || absolutePaths.Count == 0) return result;

            var sb = new System.Text.StringBuilder();
            // Order by tasks.updated_at, NOT task_file_links.created_at:
            // a task linked 6 months ago and shipped yesterday must outrank
            // a task linked yesterday and shipped 6 months ago. The link's
            // created_at reflects when the agent first attached the file
            // to the task, which can predate completion by an arbitrary
            // amount.
            //
            // Caveat: tasks.updated_at is a proxy for completion time, not
            // the completion time itself — the schema doesn't have a
            // dedicated completed_at column on the tasks table (see the
            // CREATE TABLE at TaskDatabase.cs:120 and the ALTER TABLE
            // chain through ~line 1063 — none add completed_at). Any edit
            // to a done task after completion bumps updated_at, so a task
            // completed long ago but recently edited will outrank one
            // completed yesterday and untouched. This is still strictly
            // better than f.created_at (which is link-creation time, not
            // completion-related at all). Wiring up a real CompletedAt
            // through KanbanTask + MessageBroker + SaveTask + migration is
            // a separate ticket. Adversary CRITICAL Run 2 fix.
            //
            // f.created_at + f.rowid kept as secondary tie-breaks for
            // tasks updated in the same second.
            //
            // Recency cutoff: only surface done-tasks updated within the
            // last 3 days. Without this, any stale task_file_links row
            // from a months-old done task whose file is currently
            // uncommitted in trunk would render as a phantom "shipped"
            // group in the HUD Git tab — anchored to today's working
            // tree even though the task shipped half a year ago. The
            // intent of the shipped chip is "I just marked this done
            // minutes/hours ago and the files are still uncommitted",
            // which 3 days covers comfortably (weekend-safe: a task
            // done Friday afternoon and committed Monday still
            // surfaces).
            //
            // CAVEAT — phantom resurrection: this window does NOT
            // permanently suppress phantoms. Because t.updated_at
            // bumps on EVERY task edit via SaveTask (including
            // system-driven writes — pipeline reports, review_notes,
            // checklist transitions, summary edits, continuation
            // notes), any touch of a long-shipped task whose files
            // are still uncommitted in trunk will re-surface its
            // shipped chip until 3 more days of quiescence pass. A
            // real fix needs a dedicated `completed_at` column (see
            // separate-ticket TODO at lines 1454-1465 above); the
            // window narrows the surface, but only completed_at
            // closes the foot-gun. Cross-model adversary Run 1 on
            // 57a7326f.
            //
            // Hardcoded constant rather than a setting: bounded scope,
            // promote to settings only if users start asking. Ticket
            // 57a7326f filed by cross-model adversary on a401e082
            // pipeline Run 1.
            sb.Append("SELECT f.file_path, t.id, t.title, t.assignee, t.updated_at ");
            sb.Append("FROM task_file_links f JOIN tasks t ON t.id = f.task_id ");
            // Wrap row side in datetime(t.updated_at) so SQLite parses
            // the timestamp semantically rather than relying on string
            // lexical compare. Works today via the System.Data.SQLite
            // ISO8601 default, but a future connection-string change
            // (e.g. DateTimeFormat=Ticks at TaskDatabase.cs:60-66)
            // would silently break a raw-string comparison without a
            // build error. Adversary LOW Run 1 fix.
            sb.Append("WHERE t.status = 'done' AND datetime(t.updated_at) > datetime('now', '-3 days') AND f.file_path IN (");
            for (int i = 0; i < absolutePaths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("@p").Append(i);
            }
            sb.Append(") ORDER BY t.updated_at DESC, f.created_at DESC, f.rowid DESC");

            // Only the parameter name suffixes (@p0, @p1, ...) are concatenated;
            // file path VALUES bind via SQLiteParameter below, so no injection.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sb.ToString(), _connection);
#pragma warning restore CA2100
            for (int i = 0; i < absolutePaths.Count; i++)
            {
                cmd.Parameters.AddWithValue("@p" + i, absolutePaths[i] ?? string.Empty);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string filePath = reader.GetString(0);
                if (result.ContainsKey(filePath)) continue;
                string taskId = reader.GetString(1);
                string title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                string assignee = reader.IsDBNull(3) ? null : reader.GetString(3);
                result[filePath] = (taskId, title, assignee);
            }
            return result;
        }

        /// <summary>
        /// Returns the distinct active (in_progress) task IDs that have a
        /// linkage to ANY of the given files. Unlike
        /// <see cref="GetActiveTaskLinkageForFiles"/> this does NOT dedupe to
        /// a single primary task per file — it surfaces every active claim,
        /// which is what cross-task contamination detection needs.
        ///
        /// <para>Adversary finding from item [11]: a file claimed by tasks A
        /// AND B should make the contamination banner fire even though the
        /// per-file chip can only show one. This separate query closes the
        /// gap.</para>
        /// </summary>
        public System.Collections.Generic.IReadOnlyList<string> GetDistinctActiveTaskIdsForFiles(
            System.Collections.Generic.IReadOnlyList<string> absolutePaths)
        {
            using var gate = LockConn();
            var result = new System.Collections.Generic.List<string>();
            if (absolutePaths == null || absolutePaths.Count == 0) return result;

            var sb = new System.Text.StringBuilder();
            sb.Append("SELECT DISTINCT t.id ");
            sb.Append("FROM task_file_links f JOIN tasks t ON t.id = f.task_id ");
            sb.Append("WHERE t.status = 'in_progress' AND f.file_path IN (");
            for (int i = 0; i < absolutePaths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("@p").Append(i);
            }
            sb.Append(")");

#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sb.ToString(), _connection);
#pragma warning restore CA2100
            for (int i = 0; i < absolutePaths.Count; i++)
            {
                cmd.Parameters.AddWithValue("@p" + i, absolutePaths[i] ?? string.Empty);
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }

        /// <summary>
        /// Returns the most recent <c>task_reports.verdict</c> string for a task,
        /// or <c>null</c> if the task has no reports. Drives the pipeline-status
        /// badge on uncommitted-file rows in the HUD Git tab.
        ///
        /// <para>If the task has cycled coding↔testing multiple times the
        /// most-recent-cycle verdict wins (newest <c>created_at</c> first).
        /// Empty / null verdicts are skipped.</para>
        /// </summary>
        public string GetLatestVerdictForTask(string taskId, string requiredStatus = "in_progress")
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(taskId)) return null;

            // Coerce requiredStatus to a known closed set. Anything else
            // would produce an EXISTS clause that matches nothing and silently
            // returns null — a future caller passing "todo" / "" / arbitrary
            // would get a dead chip with no error. Adversary LOW Run 2 fix.
            if (requiredStatus != "in_progress" && requiredStatus != "done")
                requiredStatus = "in_progress";
            // Re-validate that the task is still at the expected status at
            // query time (original adversary finding: linkage and verdict reads
            // are not in the same transaction, so a task could transition
            // between them — chip would advertise a verdict for a task that
            // moved on). The EXISTS clause drops the verdict to null if the
            // task no longer matches the expected status.
            //
            // requiredStatus parameterized so the shipped-tier path (chip
            // surfaces verdict for status='done' tasks) can opt into the same
            // freshness gate while reading verdicts for completed tasks.
            // Default 'in_progress' preserves prior behavior at every existing
            // call site. Run 2 fix for adversary CRITICAL Run 1.
            //
            // Deterministic tie-break via rowid for same-second writes.
            const string sql = @"
                SELECT verdict
                FROM task_reports
                WHERE task_id = @taskId
                  AND verdict IS NOT NULL AND verdict <> ''
                  AND EXISTS (SELECT 1 FROM tasks t WHERE t.id = @taskId AND t.status = @requiredStatus)
                ORDER BY created_at DESC, rowid DESC
                LIMIT 1";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@requiredStatus", requiredStatus);
            return cmd.ExecuteScalar() as string;
        }

        #endregion

        #region Task Worktrees

        /// <summary>
        /// Save (or replace) a worktree record for a task. Called when a worktree
        /// is materialized via <c>git worktree add</c> at task-activate time.
        /// Phase 1 of per-task worktree isolation — gated by
        /// <c>MULTITERMINAL_WORKTREE_MODE</c>.
        /// </summary>
        public void SaveWorktreeRecord(string taskId, string agentName, string worktreePath, string branchName, bool isCanonical)
        {
            using var gate = LockConn();
            const string sql = @"
                INSERT OR REPLACE INTO task_worktrees
                    (task_id, agent_name, worktree_path, branch_name, created_at, status, is_canonical)
                VALUES (@taskId, @agentName, @worktreePath, @branchName, @createdAt, 'active', @isCanonical)
            ";
            // The LockConn() gate (method top) serializes this with all other DB access on
            // THIS TaskDatabase instance's connection: the janitor timer thread reads these
            // task_worktrees rows concurrently with REST/MCP request threads writing them,
            // all on one SQLiteConnection. As of task ad08caac every runtime _connection user
            // in this class takes this same gate, so the earlier "SaveTask/UpdateTask not yet
            // covered" caveat no longer applies WITHIN this instance.
            // Scope caveat: this serializes one instance's handle, NOT the whole
            // multiterminal.db file — a second TaskDatabase instance (MainForm._chatTaskDatabase)
            // has its own connection + _dbLock, and 6 sibling classes share this handle
            // ungated. Global race-freedom across all users is ticket bb2b0104.
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@agentName", string.IsNullOrEmpty(agentName) ? WorktreeNaming.LegacyAgent : agentName);
            cmd.Parameters.AddWithValue("@worktreePath", worktreePath);
            cmd.Parameters.AddWithValue("@branchName", branchName);
            cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@isCanonical", isCanonical ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Maps the standard 7-column worktree projection
        /// (<c>task_id, worktree_path, branch_name, created_at, status, agent_name, is_canonical</c>)
        /// into a <see cref="MCPServer.Models.TaskWorktree"/>. All worktree readers
        /// SELECT these columns in this order so they can share this mapper.
        /// </summary>
        private static MCPServer.Models.TaskWorktree MapWorktree(System.Data.Common.DbDataReader reader)
        {
            return new MCPServer.Models.TaskWorktree
            {
                TaskId = reader.GetString(0),
                WorktreePath = reader.GetString(1),
                BranchName = reader.GetString(2),
                CreatedAt = DateTime.Parse(reader.GetString(3)),
                Status = reader.GetString(4),
                AgentName = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsCanonical = !reader.IsDBNull(6) && reader.GetInt32(6) != 0
            };
        }

        /// <summary>
        /// Look up the worktree record for a specific task. Returns null when
        /// no record exists (i.e., the task has never been activated under
        /// worktree mode).
        /// </summary>
        public MCPServer.Models.TaskWorktree GetWorktreeForTask(string taskId)
        {
            using var gate = LockConn();
            // Per-agent isolation: a task can now have multiple worktree rows
            // (assignee canonical + N helpers). This task-scoped overload returns
            // the canonical (assignee) row — the representative worktree that all
            // pre-isolation callers expect — preferring is_canonical, then most
            // recent. For a specific agent's worktree use the (taskId, agentName)
            // overload below.
            const string sql = @"
                SELECT task_id, worktree_path, branch_name, created_at, status, agent_name, is_canonical
                FROM task_worktrees
                WHERE task_id = @taskId
                ORDER BY is_canonical DESC, created_at DESC
                LIMIT 1
            ";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return MapWorktree(reader);
        }

        /// <summary>
        /// Look up the worktree record for a specific (task, agent) pair, or
        /// <c>null</c> when that agent has no worktree on the task. This is the
        /// agent-aware lookup introduced for per-agent isolation (task bab81a92).
        /// </summary>
        public MCPServer.Models.TaskWorktree GetWorktreeForTask(string taskId, string agentName)
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT task_id, worktree_path, branch_name, created_at, status, agent_name, is_canonical
                FROM task_worktrees
                WHERE task_id = @taskId AND agent_name = @agentName
            ";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@agentName", string.IsNullOrEmpty(agentName) ? WorktreeNaming.LegacyAgent : agentName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return MapWorktree(reader);
        }

        /// <summary>
        /// The most-recently-created <c>active</c> worktree row for an agent,
        /// across all tasks, or <c>null</c>. This is the per-agent "active
        /// worktree" pointer used to resolve a HELPER's active task: a helper is
        /// never the task assignee, and <c>tasks.sub_status='active'</c> is a
        /// task-level flag keyed to the assignee, so it cannot represent a
        /// helper's active task. The agent's own active <c>task_worktrees</c> row
        /// is the source of truth. The <c>agent_name = @agentName</c> filter
        /// excludes the backfilled <c>'__legacy__'</c> bucket, so a legacy row
        /// never resolves as a real agent's active worktree (task bab81a92).
        /// </summary>
        public MCPServer.Models.TaskWorktree GetActiveWorktreeForAgent(string agentName)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(agentName)) return null;
            const string sql = @"
                SELECT task_id, worktree_path, branch_name, created_at, status, agent_name, is_canonical
                FROM task_worktrees
                WHERE status = 'active' AND agent_name = @agentName
                ORDER BY created_at DESC
                LIMIT 1
            ";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@agentName", agentName);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return MapWorktree(reader);
        }

        /// <summary>
        /// All worktree records for a task (every agent, both <c>active</c> and
        /// <c>pruned</c>), canonical first then most recent. Used by the task-done
        /// teardown (commit/integrate/prune every agent worktree) and per-task
        /// reconciliation. Callers filter by <c>Status</c> as needed.
        /// </summary>
        public List<MCPServer.Models.TaskWorktree> ListWorktreesForTask(string taskId)
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT task_id, worktree_path, branch_name, created_at, status, agent_name, is_canonical
                FROM task_worktrees
                WHERE task_id = @taskId
                ORDER BY is_canonical DESC, created_at DESC
            ";
            var results = new List<MCPServer.Models.TaskWorktree>();
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(MapWorktree(reader));
            }
            return results;
        }

        /// <summary>
        /// Mark a worktree record as pruned. Called after <c>git worktree remove</c>
        /// succeeds at task-done time. The record itself is retained for audit /
        /// history (Phase 1 keeps pruned rows; a separate janitor may sweep them
        /// in a later phase).
        /// </summary>
        public void MarkWorktreePruned(string taskId)
        {
            using var gate = LockConn();
            // Task-scoped: marks EVERY worktree row for the task pruned. With
            // per-agent isolation this is the prune-all form used by the task-done
            // teardown. To prune a single agent's row, use the (taskId, agentName)
            // overload below.
            const string sql = "UPDATE task_worktrees SET status = 'pruned' WHERE task_id = @taskId";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Mark a single agent's worktree record pruned (one row of the composite
        /// key). Used when pruning per-agent worktrees individually (task bab81a92).
        /// </summary>
        public void MarkWorktreePruned(string taskId, string agentName)
        {
            using var gate = LockConn();
            const string sql = "UPDATE task_worktrees SET status = 'pruned' WHERE task_id = @taskId AND agent_name = @agentName";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@agentName", string.IsNullOrEmpty(agentName) ? WorktreeNaming.LegacyAgent : agentName);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Updates the <c>worktree_path</c> of an existing record. Used by the
        /// layout-migration startup service (task c6ed236c) after a successful
        /// <c>git worktree move</c>. No-op if no row exists for <paramref name="taskId"/>.
        /// </summary>
        public void UpdateWorktreePath(string taskId, string newWorktreePath)
        {
            using var gate = LockConn();
            const string sql = "UPDATE task_worktrees SET worktree_path = @path WHERE task_id = @taskId";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@path", newWorktreePath);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// List all worktree records currently in <c>status='active'</c>, ordered
        /// most-recent first. Used by panel aggregation and lifecycle reconciliation.
        /// </summary>
        public List<MCPServer.Models.TaskWorktree> ListActiveWorktrees()
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT task_id, worktree_path, branch_name, created_at, status, agent_name, is_canonical
                FROM task_worktrees
                WHERE status = 'active'
                ORDER BY created_at DESC
            ";
            var results = new List<MCPServer.Models.TaskWorktree>();
            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(MapWorktree(reader));
            }
            return results;
        }

        /// <summary>
        /// Every <c>worktree_path</c> ever recorded — both <c>active</c> and
        /// <c>pruned</c>. Used by the janitor's orphan empty-dir sweep (Pass 3)
        /// to derive the set of parent directories where worktrees have lived,
        /// then enumerate sibling dirs and rmdir empty orphans. Returns raw
        /// paths only (no metadata) since callers just need the set.
        /// </summary>
        public List<string> ListAllWorktreePaths()
        {
            using var gate = LockConn();
            const string sql = "SELECT worktree_path FROM task_worktrees WHERE worktree_path IS NOT NULL AND worktree_path <> ''";
            var results = new List<string>();
            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }
            return results;
        }

        /// <summary>
        /// Worktree records that are <c>status='active'</c> but whose owning
        /// task is <c>done</c>. Used by the janitor's deferred-prune retry
        /// pass — when the broker's pre-prune broadcast times out and the
        /// prune is deferred, the worktree is left active; the janitor finds
        /// these rows on a later sweep and retries the prune once agents
        /// have likely released their cwd. Task db4b18c6 cycle-3.
        /// </summary>
        public List<MCPServer.Models.TaskWorktree> ListActiveWorktreesForDoneTasks()
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT w.task_id, w.worktree_path, w.branch_name, w.created_at, w.status, w.agent_name, w.is_canonical
                FROM task_worktrees w
                JOIN tasks t ON t.id = w.task_id
                WHERE w.status = 'active' AND t.status = 'done'
                ORDER BY w.created_at DESC
            ";
            var results = new List<MCPServer.Models.TaskWorktree>();
            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(MapWorktree(reader));
            }
            return results;
        }

        /// <summary>
        /// List worktree records that are <c>pruned</c> but whose owning task is
        /// <c>done</c>. These are the rows the Phase 4 janitor inspects: if the
        /// task branch (<c>task/{taskIdShort}</c>) still exists in git, the merge
        /// never happened and the dev needs to resolve manually.
        /// </summary>
        public List<MCPServer.Models.TaskWorktree> ListPrunedWorktreesForDoneTasks()
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT w.task_id, w.worktree_path, w.branch_name, w.created_at, w.status, w.agent_name, w.is_canonical
                FROM task_worktrees w
                JOIN tasks t ON t.id = w.task_id
                WHERE w.status = 'pruned' AND t.status = 'done'
                ORDER BY w.created_at DESC
            ";
            var results = new List<MCPServer.Models.TaskWorktree>();
            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(MapWorktree(reader));
            }
            return results;
        }

        /// <summary>
        /// Tasks whose recorded worktree branch matches <paramref name="branchName"/>,
        /// scoped to <paramref name="projectId"/>. Returns (id, title) pairs ordered by
        /// most-recent worktree first. Used by the HUD Git tree to surface a branch's
        /// task cluster in the inspector + by BranchMetadataController.GetDraftContext
        /// to source agent prompt content. Empty list when nothing is linked OR when
        /// either argument is empty.
        ///
        /// <para>Project scoping is mandatory (security): branch names like 'main' or
        /// 'master' are reused across projects, so a branch-name-only lookup would
        /// leak task content from unrelated projects through draft-context. Pipeline
        /// Run 2 finding from both Codex security-auditor + cross-model adversary.</para>
        /// </summary>
        public List<(string Id, string Title)> GetTasksLinkedToBranch(string projectId, string branchName)
        {
            using var gate = LockConn();
            var results = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(projectId)) return results;
            if (string.IsNullOrWhiteSpace(branchName)) return results;

            // Pipeline Run 5 finding (Codex adversary MEDIUM): without serialization this
            // helper races against concurrent TaskDatabase operations (called from
            // HudGitRenderer.RefreshAsync background pass + REST controllers) on the same
            // SQLiteConnection. The LockConn() gate above closes that — the same one-idiom
            // gate every runtime method in this class now takes (task ad08caac).
            const string sql = @"
                SELECT t.id, t.title
                FROM tasks t
                JOIN task_worktrees w ON t.id = w.task_id
                WHERE w.branch_name = @branchName AND t.project_id = @projectId
                ORDER BY w.created_at DESC
            ";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@branchName", branchName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
            }
            return results;
        }

        #endregion

        /// <summary>
        /// Update a task's title and description.
        /// </summary>
        /// <returns>True if a task was updated, false if task not found.</returns>
        public bool UpdateTask(string taskId, string title, string description)
        {
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
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

        /// <summary>
        /// Get the active in-progress task for a specific agent.
        /// Returns null if no active task found.
        /// </summary>
        public KanbanTask GetActiveTaskForAgent(string agentName)
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
                FROM tasks
                WHERE assignee = @agentName COLLATE NOCASE
                  AND status = 'in_progress'
                  AND sub_status = 'active'
                ORDER BY updated_at DESC
                LIMIT 1
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@agentName", agentName);
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
            using var gate = LockConn();
            var tasks = new List<KanbanTask>();
            var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
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
            using var gate = LockConn();
            var tasks = new List<KanbanTask>();
            var fourteenDaysAgo = DateTime.UtcNow.AddDays(-14);

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at, project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
            var tasks = new List<KanbanTask>();
            var cutoffDate = DateTime.UtcNow.AddDays(-minDaysPaused);

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at,
                       project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
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
            using var gate = LockConn();
            var tasks = new List<KanbanTask>();

            const string sql = @"
                SELECT id, title, description, status, assignee, created_by, created_at, updated_at,
                       project_id, sub_status, paused_at, flagged_stale_at, stale_level, stale_notified_at, stale_response, priority, checklist_json, plan, implementation_summary, test_results, implementation_checklist_json, continuation_notes, auto_status, review_notes, is_quick_task, sort_order
                FROM tasks
                WHERE assignee = @assignee COLLATE NOCASE AND stale_level > 0
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
        /// Seed default agent profiles if the profiles table is empty.
        /// Only runs on first launch — existing profiles are never overwritten.
        /// </summary>
        private void SeedDefaultProfiles()
        {
            const string countSql = "SELECT COUNT(*) FROM team_member_profiles";
            using var countCmd = new SQLiteCommand(countSql, _connection);
            var count = Convert.ToInt64(countCmd.ExecuteScalar());
            if (count > 0) return;

            var now = DateTime.UtcNow;
            var defaults = new[]
            {
                new TeamMemberProfile
                {
                    Id = "Alice",
                    DisplayName = "Alice",
                    Role = "Backend Engineer",
                    Bio = "Specializes in services, databases, APIs, and server-side logic. Methodical and thorough.",
                    SkillsJson = "[\"C#\",\".NET\",\"SQL\",\"REST APIs\",\"SQLite\",\"Entity Framework\",\"Performance Optimization\"]",
                    InterestsJson = "[\"Clean Architecture\",\"Database Design\",\"Testing\"]",
                    PreferredModel = "sonnet",
                    IsTeamLead = true,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new TeamMemberProfile
                {
                    Id = "Bob",
                    DisplayName = "Bob",
                    Role = "Frontend Engineer",
                    Bio = "Focuses on UI, user experience, HTML/CSS/JS panels, and WebView2 integration. Creative and detail-oriented.",
                    SkillsJson = "[\"HTML\",\"CSS\",\"JavaScript\",\"WebView2\",\"WinForms\",\"WPF\",\"UI/UX Design\"]",
                    InterestsJson = "[\"Responsive Design\",\"Accessibility\",\"Animation\"]",
                    PreferredModel = "sonnet",
                    IsTeamLead = true,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new TeamMemberProfile
                {
                    Id = "Charlie",
                    DisplayName = "Charlie",
                    Role = "DevOps Engineer",
                    Bio = "Handles builds, deployments, CI/CD, installers, hooks, and infrastructure. Practical and systematic.",
                    SkillsJson = "[\"PowerShell\",\"Bash\",\"Git\",\"CI/CD\",\"Inno Setup\",\"Node.js\",\"Docker\"]",
                    InterestsJson = "[\"Automation\",\"Monitoring\",\"Reliability\"]",
                    PreferredModel = "sonnet",
                    IsTeamLead = true,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new TeamMemberProfile
                {
                    Id = "Diana",
                    DisplayName = "Diana",
                    Role = "Software Architect",
                    Bio = "Designs system architecture, data models, integration patterns, and cross-cutting concerns. Strategic thinker.",
                    SkillsJson = "[\"System Design\",\"Design Patterns\",\"C#\",\"Data Modeling\",\"API Design\",\"Refactoring\"]",
                    InterestsJson = "[\"Scalability\",\"Code Quality\",\"Domain-Driven Design\"]",
                    PreferredModel = "sonnet",
                    IsTeamLead = true,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new TeamMemberProfile
                {
                    Id = "Eve",
                    DisplayName = "Eve",
                    Role = "QA Engineer",
                    Bio = "Designs test strategies, writes test cases, validates edge cases, and ensures quality. Meticulous and skeptical.",
                    SkillsJson = "[\"Test Design\",\"Integration Testing\",\"Edge Cases\",\"Regression Testing\",\"Bug Triage\",\"Acceptance Criteria\"]",
                    InterestsJson = "[\"Quality Assurance\",\"Test Automation\",\"Exploratory Testing\"]",
                    PreferredModel = "sonnet",
                    IsTeamLead = true,
                    CreatedAt = now,
                    UpdatedAt = now
                },
                new TeamMemberProfile
                {
                    Id = "Frank",
                    DisplayName = "Frank",
                    Role = "Technical Writer",
                    Bio = "Creates documentation, API references, user guides, and architectural decision records. Clear and concise communicator.",
                    SkillsJson = "[\"Technical Writing\",\"Markdown\",\"API Documentation\",\"Architecture Docs\",\"User Guides\",\"Diagrams\"]",
                    InterestsJson = "[\"Developer Experience\",\"Knowledge Management\",\"Onboarding\"]",
                    PreferredModel = "sonnet",
                    IsTeamLead = true,
                    CreatedAt = now,
                    UpdatedAt = now
                }
            };

            foreach (var profile in defaults)
            {
                SaveProfile(profile);
            }

            System.Diagnostics.Debug.WriteLine($"[TaskDatabase] Seeded {defaults.Length} default agent profiles");
        }

        /// <summary>
        /// Get a team member profile by ID.
        /// </summary>
        public TeamMemberProfile GetProfile(string profileId)
        {
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            using var gate = LockConn();
            const string sql = @"
                INSERT INTO session_lineage
                    (session_id, parent_session_id, task_id, agent_name, session_type, summary, session_file_path, started_at, ended_at, created_at,
                     processing_status, project_path, indexed_at, summarized_at)
                VALUES
                    (@sessionId, @parentSessionId, @taskId, @agentName, @sessionType, @summary, @sessionFilePath, @startedAt, @endedAt, @createdAt,
                     COALESCE(@processingStatus, 'complete'), @projectPath, @indexedAt, @summarizedAt)
                ON CONFLICT(session_id) DO UPDATE SET
                    parent_session_id = @parentSessionId,
                    task_id           = COALESCE(@taskId, task_id),
                    agent_name        = @agentName,
                    session_type      = @sessionType,
                    summary           = CASE WHEN summary IS NOT NULL AND summary != '' THEN summary ELSE COALESCE(@summary, summary) END,
                    session_file_path = COALESCE(@sessionFilePath, session_file_path),
                    started_at        = COALESCE(@startedAt, started_at),
                    ended_at          = COALESCE(@endedAt, ended_at),
                    processing_status = CASE WHEN @processingStatus IS NOT NULL THEN @processingStatus ELSE processing_status END,
                    project_path      = COALESCE(@projectPath, project_path),
                    indexed_at        = COALESCE(@indexedAt, indexed_at),
                    summarized_at     = COALESCE(@summarizedAt, summarized_at)
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
            command.Parameters.AddWithValue("@processingStatus", (object)record.ProcessingStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("@projectPath", (object)record.ProjectPath ?? DBNull.Value);
            command.Parameters.AddWithValue("@indexedAt", (object)record.IndexedAt ?? DBNull.Value);
            command.Parameters.AddWithValue("@summarizedAt", (object)record.SummarizedAt ?? DBNull.Value);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns all session IDs that have already been imported into the lineage table.
        /// Used to skip re-importing existing sessions during bulk sync.
        /// </summary>
        public HashSet<string> GetImportedSessionIds()
        {
            using var gate = LockConn();
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
        /// Looks up the agent name for a session from the session_agent_map table.
        /// Returns null if no mapping exists.
        /// </summary>
        public string GetSessionAgentName(string sessionId)
        {
            using var gate = LockConn();
            const string sql = "SELECT agent_name FROM session_agent_map WHERE session_id = @sessionId";
            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            var result = command.ExecuteScalar();
            return result as string;
        }

        /// <summary>
        /// Snapshot the entire session_agent_map (session_id -&gt; agent_name) in ONE
        /// locked query. Identity discovery uses this so the dropdown-build path does
        /// a single read instead of one point-query per session file — the N+1 the
        /// per-session <see cref="GetSessionAgentName"/> would cause on a WinForms UI
        /// path (task 4558fa6b).
        /// </summary>
        public Dictionary<string, string> GetAllSessionAgentNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var gate = LockConn();
            const string sql = "SELECT session_id, agent_name FROM session_agent_map WHERE session_id IS NOT NULL AND agent_name IS NOT NULL";
            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                map[reader.GetString(0)] = reader.GetString(1);
            }

            return map;
        }

        /// <summary>
        /// Returns the set of session IDs that are currently marked as active
        /// in the session_agent_map table (is_active = 1).
        /// </summary>
        public HashSet<string> GetActiveSessionIds()
        {
            using var gate = LockConn();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string sql = "SELECT session_id FROM session_agent_map WHERE is_active = 1";
            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
            return ids;
        }

        /// <summary>
        /// Marks stale active sessions as inactive. Sessions that have been "active" for more than
        /// 24 hours are clearly abandoned — their terminal closed without properly deactivating.
        /// Returns the number of rows cleaned up.
        /// </summary>
        public int CleanupStaleActiveSessions()
        {
            using var gate = LockConn();
            const string sql = @"
                UPDATE session_agent_map
                SET is_active = 0
                WHERE is_active = 1
                  AND started_at < datetime('now', '-24 hours')
            ";
            using var command = new SQLiteCommand(sql, _connection);
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Gets all session lineage records linked to a task, ordered newest first.
        /// </summary>
        public List<SessionLineageRecord> GetSessionsByTask(string taskId)
        {
            using var gate = LockConn();
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
            using var gate = LockConn();
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
            // Whole transaction serialized on _dbLock (runtime site: session-import Task.Run threads).
            using var gate = LockConn();
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
            using var gate = LockConn();
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
                    string ftsQuery = SanitizeFts5Query(query);
                    return ExecuteFts5Search(ftsQuery, sessionId, taskId, role, agentName, limit);
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
            using var gate = LockConn();
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

            // CA2100: SQL is composed from static literal fragments only (no user input concatenated);
            // all user-supplied values flow through SQLiteParameter via AddWithValue below.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
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
            using var gate = LockConn();
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

            // CA2100: SQL is composed from static literal fragments only (no user input concatenated);
            // all user-supplied values flow through SQLiteParameter via AddWithValue below.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
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
        /// Sanitize a natural-language query for FTS5.
        /// Splits into keywords, removes noise words and punctuation, joins with OR
        /// so any keyword can match (not just exact phrases).
        /// </summary>
        private static string SanitizeFts5Query(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "\"\"";

            var cleaned = query.Replace("\"", " ").Replace("*", " ").Replace("(", " ")
                               .Replace(")", " ").Replace(":", " ").Replace("^", " ")
                               .Replace("'", " ").Replace("\u2019", " ");

            var noiseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
                "in", "on", "at", "to", "for", "of", "with", "by", "from", "and",
                "or", "not", "no", "but", "if", "then", "than", "that", "this",
                "it", "its", "i", "we", "you", "they", "he", "she", "my", "your",
                "do", "did", "didn", "t", "don", "does", "has", "have", "had",
                "will", "would", "could", "should", "can", "may", "might"
            };

            var words = cleaned.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var keywords = new List<string>();
            foreach (var w in words)
            {
                if (w.Length >= 2 && !noiseWords.Contains(w))
                    keywords.Add("\"" + w + "\"");
            }

            if (keywords.Count == 0) return "\"" + query.Replace("\"", " ") + "\"";

            return string.Join(" OR ", keywords);
        }

        /// <summary>
        /// Returns the most recent session lineage record whose session_file_path starts with
        /// the given folder prefix. Ordered by ended_at DESC, then created_at DESC.
        /// Returns null if no matching record is found.
        /// </summary>
        public SessionLineageRecord GetMostRecentSessionByFolder(string claudeProjectFolder, string agentName = null, string excludeSessionId = null, int skip = 0)
        {
            using var gate = LockConn();
            // Normalize: ensure trailing separator so we don't match sibling folders sharing a prefix
            string folderPrefix = claudeProjectFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            string sql = @"
                SELECT id, session_id, parent_session_id, task_id, agent_name, session_type,
                       summary, session_file_path, started_at, ended_at, created_at,
                       processing_status, project_path, indexed_at, summarized_at
                FROM session_lineage
                WHERE (session_file_path LIKE @folder || '%' OR project_path = @projectPath)
                  AND session_type != 'subagent'";

            if (!string.IsNullOrEmpty(agentName))
                sql += " AND agent_name = @agentName";

            if (!string.IsNullOrEmpty(excludeSessionId))
                sql += " AND session_id != @excludeSessionId";

            sql += $@"
                ORDER BY ended_at DESC, created_at DESC
                LIMIT 1 OFFSET {Math.Max(0, skip)}";

            // CA2100: SQL is composed from static literals plus an int-clamped OFFSET
            // (Math.Max(0, skip) forces a non-negative int, not a string); all user-supplied
            // string values flow through SQLiteParameter via AddWithValue below.
#pragma warning disable CA2100
            using var command = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("@folder", folderPrefix);
            command.Parameters.AddWithValue("@projectPath", claudeProjectFolder.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(agentName))
                command.Parameters.AddWithValue("@agentName", agentName);
            if (!string.IsNullOrEmpty(excludeSessionId))
                command.Parameters.AddWithValue("@excludeSessionId", excludeSessionId);
            using var reader = command.ExecuteReader();

            return reader.Read() ? ReadSessionLineageFromReader(reader) : null;
        }

        /// <summary>
        /// Returns sessions that have no summary, excluding subagent sessions.
        /// Ordered by ended_at DESC (most recent first). Limited to a configurable count.
        /// </summary>
        public List<SessionLineageRecord> GetUnsummarizedSessions(string claudeProjectFolder, int limit = 10)
        {
            using var gate = LockConn();
            var results = new List<SessionLineageRecord>();

            string folderPrefix = claudeProjectFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            const string sql = @"
                SELECT id, session_id, parent_session_id, task_id, agent_name, session_type,
                       summary, session_file_path, started_at, ended_at, created_at
                FROM session_lineage
                WHERE (summary IS NULL OR summary = '')
                  AND session_type != 'subagent'
                  AND session_file_path LIKE @folder || '%'
                  AND COALESCE(ended_at, started_at, created_at) >= @cutoff
                ORDER BY ended_at DESC, created_at DESC
                LIMIT @limit
            ";

            var cutoff = DateTime.UtcNow.AddDays(-14).ToString("O");

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@folder", folderPrefix);
            command.Parameters.AddWithValue("@cutoff", cutoff);
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSessionLineageFromReader(reader));
            }

            return results;
        }

        /// <summary>
        /// Updates the summary field for a session lineage record.
        /// Returns the number of rows affected (0 if sessionId not found).
        /// </summary>
        public int UpdateSessionSummary(string sessionId, string summary)
        {
            using var gate = LockConn();
            const string sql = "UPDATE session_lineage SET summary = @summary WHERE session_id = @sessionId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@summary", (object)summary ?? DBNull.Value);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Updates the processing_status and optional timestamp for a session lifecycle transition.
        /// </summary>
        public int UpdateSessionProcessingStatus(string sessionId, string status, string timestampColumn = null)
        {
            using var gate = LockConn();
            string sql = "UPDATE session_lineage SET processing_status = @status";
            if (!string.IsNullOrEmpty(timestampColumn))
            {
                // Whitelist: timestampColumn is only ever "indexed_at" or "summarized_at"
                // from internal callers in SessionLineageService. Reject anything else to
                // make this explicit at the SQL-composition boundary.
                if (timestampColumn != "indexed_at" && timestampColumn != "summarized_at")
                    throw new ArgumentException(
                        "timestampColumn must be 'indexed_at' or 'summarized_at'.",
                        nameof(timestampColumn));
                sql += $", {timestampColumn} = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')";
            }
            sql += " WHERE session_id = @sessionId";

            // CA2100: SQL column fragment comes from a hard-coded whitelist above
            // ("indexed_at"/"summarized_at"); all user-supplied values flow through
            // SQLiteParameter via AddWithValue below.
#pragma warning disable CA2100
            using var command = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns a single session lineage record by session_id, or null if not found.
        /// </summary>
        public SessionLineageRecord GetSessionById(string sessionId)
        {
            using var gate = LockConn();
            const string sql = @"
                SELECT id, session_id, parent_session_id, task_id, agent_name, session_type,
                       summary, session_file_path, started_at, ended_at, created_at,
                       processing_status, project_path, indexed_at, summarized_at
                FROM session_lineage
                WHERE session_id = @sessionId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@sessionId", sessionId);
            using var reader = command.ExecuteReader();

            return reader.Read() ? ReadSessionLineageFromReader(reader) : null;
        }

        /// <summary>
        /// Closes all 'open' sessions for the given agent and project.
        /// Sets processing_status='closed' and ended_at to now.
        /// Optionally excludes a specific session (the current one).
        /// </summary>
        public int CloseOpenSessions(string agentName, string projectPath, string excludeSessionId = null)
        {
            using var gate = LockConn();
            string sql = @"
                UPDATE session_lineage
                SET processing_status = 'closed',
                    ended_at = COALESCE(ended_at, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                WHERE agent_name = @agentName
                  AND processing_status = 'open'";

            if (!string.IsNullOrEmpty(projectPath))
                sql += " AND project_path = @projectPath";

            if (!string.IsNullOrEmpty(excludeSessionId))
                sql += " AND session_id != @excludeSessionId";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@agentName", agentName);
            if (!string.IsNullOrEmpty(projectPath))
                command.Parameters.AddWithValue("@projectPath", projectPath);
            if (!string.IsNullOrEmpty(excludeSessionId))
                command.Parameters.AddWithValue("@excludeSessionId", excludeSessionId);

            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns all session IDs that have no summary (null or empty).
        /// Used by BackfillSummaries to find sessions needing heuristic summaries.
        /// </summary>
        public List<string> GetAllUnsummarizedSessionIds()
        {
            using var gate = LockConn();
            var results = new List<string>();
            const string sql = "SELECT session_id FROM session_lineage WHERE summary IS NULL OR summary = ''";
            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(reader.GetString(0));
            return results;
        }

        /// <summary>
        /// Returns session IDs whose existing summary matches known-junk patterns
        /// (tool-result echoes, hook markers, trace prefixes). Used by BackfillSummaries
        /// in regenerate mode to repair historical summaries.
        /// </summary>
        public List<string> GetJunkSummarizedSessionIds()
        {
            using var gate = LockConn();
            var results = new List<string>();
            const string sql = @"
                SELECT session_id FROM session_lineage
                WHERE summary IS NOT NULL AND summary != ''
                  AND (
                       summary LIKE '✅%'
                    OR summary LIKE '❌%'
                    OR summary LIKE '📋%'
                    OR summary LIKE '⚠️%'
                    OR summary LIKE '📌%'
                    OR summary LIKE '🔧%'
                    OR summary LIKE '📝%'
                    OR summary LIKE '📊%'
                    OR summary LIKE '🧪%'
                    OR summary LIKE '⬜%'
                    OR summary LIKE '[Tool:%'
                    OR summary LIKE '[Result]%'
                    OR summary LIKE '[DEBUG]%'
                    OR summary LIKE '[Assistant]%'
                    OR summary LIKE 'Terminal registered%'
                    OR summary LIKE 'Terminal ID:%'
                    OR summary LIKE 'Found %'
                    OR summary LIKE 'No matches found%'
                    OR summary LIKE 'No matching %'
                    OR summary LIKE 'No active task for %'
                    OR summary LIKE 'Error:%'
                    OR summary LIKE 'Tool result%'
                    OR summary LIKE 'Checklist item%'
                    OR summary LIKE '%SessionStart:startup%'
                    OR summary LIKE '%AUTO-RUN SKILL:%'
                    OR summary LIKE '%MultiTerminal Agent Rules%'
                    OR summary LIKE '%Terminal Identity:%'
                    OR summary LIKE '%<persisted-output%'
                    OR summary LIKE '%<system-reminder%'
                    OR summary LIKE 'Latest Session:%'
                    OR summary LIKE 'Session registered%'
                    OR summary LIKE 'Session %mapped to%'
                    OR summary LIKE 'Profile %created%'
                    OR summary LIKE 'Profile %marked online%'
                    OR summary LIKE 'Active Task:%'
                    OR summary LIKE 'Exit code %'
                    OR summary LIKE 'ls: %'
                    OR summary LIKE 'bash: %'
                    OR summary LIKE 'cat: %'
                    OR summary LIKE 'rm: %'
                    OR summary LIKE 'cp: %'
                    OR summary LIKE 'mv: %'
                    OR summary LIKE 'mkdir: %'
                    OR summary LIKE 'cd: %'
                    OR summary LIKE 'sh: %'
                    OR summary LIKE '/bin/%'
                    OR summary LIKE 'No such file or directory%'
                    OR summary LIKE 'cannot access%'
                    OR summary LIKE 'permission denied%'
                    OR summary LIKE '<tool_use_error>%'
                    OR summary LIKE '<tool_result_error>%'
                    OR summary LIKE '{""success"":%'
                    OR summary LIKE '{""error"":%'
                    OR summary LIKE '{""updated"":%'
                    OR summary LIKE '1→%'
                    OR summary LIKE '1	%'
                    OR summary LIKE 'The user doesn''t want to proceed%'
                    OR summary LIKE 'Command running in background with ID:%'
                    OR summary LIKE 'No images attached to this checklist item%'
                    OR summary LIKE 'The file %has been updated successfully%'
                    OR summary LIKE 'The file %has been created successfully%'
                    OR summary LIKE '[Request interrupted%'
                    OR summary LIKE '-rwx%'
                    OR summary LIKE '-rw-%'
                    OR summary LIKE 'drwx%'
                    OR summary LIKE 'lrwx%'
                    OR summary LIKE 'H:/%'
                    OR summary LIKE 'C:/%'
                    OR summary LIKE 'D:/%'
                    OR summary LIKE 'H:\%'
                    OR summary LIKE 'C:\%'
                    OR summary LIKE 'D:\%'
                    OR summary LIKE '/usr/%'
                    OR summary LIKE '/home/%'
                    OR summary LIKE '/var/%'
                    OR summary LIKE '/etc/%'
                    OR summary LIKE '%==========%'
                    OR summary LIKE 'diff --git %'
                    OR summary LIKE '--- a/%'
                    OR summary LIKE '+++ b/%'
                    OR (summary GLOB '[0-9]*' AND substr(summary, 1, 20) LIKE '%:    %')
                    OR (summary LIKE '%ID: ________-____-____-____-____________%'
                        AND (summary LIKE '%Agent:%' OR summary LIKE '%Status:%' OR summary LIKE '%Started:%'))
                  )";
            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(reader.GetString(0));
            return results;
        }

        /// <summary>
        /// Returns the last N messages for a session, filtered to the given role, ordered
        /// by message_index DESC (most recent first). Useful for generating session summaries.
        /// </summary>
        public List<SessionMessageRecord> GetRecentSessionMessages(string sessionId, string role, int limit)
        {
            using var gate = LockConn();
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
        /// Returns all messages for a given session_id, ordered by message_index ASC.
        /// </summary>
        public List<SessionMessageRecord> GetSessionMessagesBySessionId(string sessionId, int limit = 500)
        {
            using var gate = LockConn();
            var results = new List<SessionMessageRecord>();

            const string sql = @"
                SELECT id, session_id, task_id, agent_name, message_index,
                       role, content, tool_name, timestamp
                FROM session_messages
                WHERE session_id = @sessionId
                ORDER BY message_index ASC
                LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
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
            using var gate = LockConn();
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
            var record = new SessionLineageRecord
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

            // Read new lifecycle columns by name (safe for queries that don't SELECT them)
            try
            {
                int ord = reader.GetOrdinal("processing_status");
                record.ProcessingStatus = reader.IsDBNull(ord) ? "complete" : reader.GetString(ord);
            }
            catch { record.ProcessingStatus = "complete"; }

            try
            {
                int ord = reader.GetOrdinal("project_path");
                record.ProjectPath = reader.IsDBNull(ord) ? null : reader.GetString(ord);
            }
            catch { }

            try
            {
                int ord = reader.GetOrdinal("indexed_at");
                record.IndexedAt = reader.IsDBNull(ord) ? null : reader.GetString(ord);
            }
            catch { }

            try
            {
                int ord = reader.GetOrdinal("summarized_at");
                record.SummarizedAt = reader.IsDBNull(ord) ? null : reader.GetString(ord);
            }
            catch { }

            return record;
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

        #region Knowledge Base

        /// <summary>
        /// Migration: ensures knowledge_entries_fts virtual table and sync triggers exist.
        /// Uses the same FTS5 + trigger pattern as MigrateAddSessionLineage.
        /// Falls back gracefully if FTS5 is not compiled into this SQLite build.
        /// </summary>
        private void MigrateAddKnowledgeBase()
        {
            // Attempt to create the FTS5 virtual table and its sync triggers.
            // Wrapped in try/catch because FTS5 is a compile-time SQLite option that may be absent.
            try
            {
                const string fts5Sql = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_entries_fts USING fts5(
                        title,
                        content,
                        tags,
                        content='knowledge_entries',
                        content_rowid='id',
                        tokenize='porter'
                    );

                    -- Trigger: keep FTS index in sync on INSERT
                    CREATE TRIGGER IF NOT EXISTS knowledge_entries_ai
                    AFTER INSERT ON knowledge_entries BEGIN
                        INSERT INTO knowledge_entries_fts(rowid, title, content, tags)
                        VALUES (new.id, new.title, new.content, new.tags);
                    END;

                    -- Trigger: keep FTS index in sync on DELETE
                    CREATE TRIGGER IF NOT EXISTS knowledge_entries_ad
                    AFTER DELETE ON knowledge_entries BEGIN
                        INSERT INTO knowledge_entries_fts(knowledge_entries_fts, rowid, title, content, tags)
                        VALUES ('delete', old.id, old.title, old.content, old.tags);
                    END;

                    -- Trigger: keep FTS index in sync on UPDATE
                    CREATE TRIGGER IF NOT EXISTS knowledge_entries_au
                    AFTER UPDATE ON knowledge_entries BEGIN
                        INSERT INTO knowledge_entries_fts(knowledge_entries_fts, rowid, title, content, tags)
                        VALUES ('delete', old.id, old.title, old.content, old.tags);
                        INSERT INTO knowledge_entries_fts(rowid, title, content, tags)
                        VALUES (new.id, new.title, new.content, new.tags);
                    END;
                ";
                using var cmd = new SQLiteCommand(fts5Sql, _connection);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskDatabase] FTS5 not available, knowledge search will use LIKE fallback: {ex.Message}");
            }
        }

        private void MigrateAddTaskRelationships()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='task_relationships'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS task_relationships (
                    id TEXT PRIMARY KEY,
                    source_task_id TEXT NOT NULL,
                    target_task_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    created_by TEXT,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(source_task_id, target_task_id, type)
                );
                CREATE INDEX IF NOT EXISTS idx_task_rel_source ON task_relationships(source_task_id);
                CREATE INDEX IF NOT EXISTS idx_task_rel_target ON task_relationships(target_task_id);
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        private void MigrateAddTaskFileLinks()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='task_file_links'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS task_file_links (
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    description TEXT,
                    line_start INTEGER,
                    line_end INTEGER,
                    added_by TEXT,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    UNIQUE(task_id, file_path)
                );
                CREATE INDEX IF NOT EXISTS idx_task_files_task ON task_file_links(task_id);
            ";
            using var createFileLinksCmd = new SQLiteCommand(createSql, _connection);
            createFileLinksCmd.ExecuteNonQuery();
        }

        // Adds nullable checklist_item_index column so a file link can be either
        // task-scoped (NULL — applies to all items, default + backward compat)
        // or item-scoped (0-based index — only that item touches this file).
        // Used by per-item review-note routing in HandleCodeReviewVerdict
        // (task 87ee90c3): a comment on file X only bounces items linked to X.
        private void MigrateAddChecklistItemIndexToFileLinks()
        {
            bool hasColumn = false;
            using (var pragmaCmd = new SQLiteCommand("PRAGMA table_info(task_file_links)", _connection))
            using (var reader = pragmaCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var columnName = reader.GetString(1);
                    if (columnName.Equals("checklist_item_index", StringComparison.OrdinalIgnoreCase))
                    {
                        hasColumn = true;
                        break;
                    }
                }
            }

            if (!hasColumn)
            {
                const string sql = "ALTER TABLE task_file_links ADD COLUMN checklist_item_index INTEGER";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();
            }
        }

        private void MigrateAddBranchMetadata()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='branch_metadata'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS branch_metadata (
                    project_id TEXT NOT NULL,
                    branch_name TEXT NOT NULL,
                    outcome TEXT,
                    drafted_by TEXT,
                    updated_at DATETIME DEFAULT (datetime('now')),
                    PRIMARY KEY (project_id, branch_name)
                );
                CREATE INDEX IF NOT EXISTS idx_branch_metadata_project ON branch_metadata(project_id);
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        private void MigrateAddOwnerProfile()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='owner_profile'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS owner_profile (
                    id TEXT PRIMARY KEY DEFAULT 'owner',
                    full_name TEXT,
                    email TEXT,
                    github_username TEXT,
                    has_github_token INTEGER NOT NULL DEFAULT 0,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    updated_at DATETIME NOT NULL DEFAULT (datetime('now'))
                );
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        private void MigrateAddSourceControlAccounts()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='source_control_accounts'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS source_control_accounts (
                    id TEXT PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    provider TEXT NOT NULL DEFAULT 'github',
                    username TEXT NOT NULL,
                    has_token INTEGER NOT NULL DEFAULT 0,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    updated_at DATETIME NOT NULL DEFAULT (datetime('now'))
                );
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        private void MigrateAddAgentInvocations()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='agent_invocations'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS agent_invocations (
                    id TEXT PRIMARY KEY,
                    agent_name TEXT NOT NULL,
                    task_id TEXT,
                    invoked_by TEXT,
                    model_used TEXT,
                    verdict TEXT,
                    score INTEGER,
                    findings_count INTEGER DEFAULT 0,
                    duration_ms INTEGER,
                    invoked_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    completed_at DATETIME,
                    report_summary TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_agent_inv_name ON agent_invocations(agent_name);
                CREATE INDEX IF NOT EXISTS idx_agent_inv_task ON agent_invocations(task_id);
                CREATE INDEX IF NOT EXISTS idx_agent_inv_date ON agent_invocations(invoked_at);
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        private void MigrateAddTaskReports()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='task_reports'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS task_reports (
                    id TEXT PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    invocation_id TEXT,
                    agent_name TEXT NOT NULL,
                    report_type TEXT NOT NULL DEFAULT 'html',
                    report_content TEXT NOT NULL,
                    verdict TEXT,
                    score INTEGER,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    created_by TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_task_reports_task ON task_reports(task_id);
                CREATE INDEX IF NOT EXISTS idx_task_reports_agent ON task_reports(agent_name);
            ";
            using var createCmd2 = new SQLiteCommand(createSql, _connection);
            createCmd2.ExecuteNonQuery();
        }

        private void MigrateAddNotificationEvents()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='notification_events'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS notification_events (
                    id TEXT PRIMARY KEY,
                    notification_type TEXT NOT NULL,
                    title TEXT NOT NULL,
                    message TEXT,
                    session_id TEXT,
                    agent_name TEXT,
                    cwd TEXT,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    read_at DATETIME
                );
                CREATE INDEX IF NOT EXISTS idx_notif_type ON notification_events(notification_type);
                CREATE INDEX IF NOT EXISTS idx_notif_created ON notification_events(created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_notif_agent ON notification_events(agent_name);
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Migration to add message_images table for ephemeral images sent via chat messages.
        /// </summary>
        private void MigrateAddMessageImages()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='message_images'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS message_images (
                    id TEXT PRIMARY KEY,
                    batch_id TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    mime_type TEXT NOT NULL,
                    image_data TEXT NOT NULL,
                    file_size_bytes INTEGER DEFAULT 0,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_message_images_batch ON message_images(batch_id);
                CREATE INDEX IF NOT EXISTS idx_message_images_created ON message_images(created_at DESC);
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Migration to add query_hash column to knowledge_entries for research cache deduplication.
        /// </summary>
        private void MigrateAddKnowledgeQueryHash()
        {
            const string checkSql = "PRAGMA table_info(knowledge_entries)";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            using var reader = checkCmd.ExecuteReader();
            bool hasQueryHash = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "query_hash", StringComparison.OrdinalIgnoreCase))
                { hasQueryHash = true; break; }
            }
            reader.Close();

            if (!hasQueryHash)
            {
                const string sql = "ALTER TABLE knowledge_entries ADD COLUMN query_hash TEXT";
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.ExecuteNonQuery();

                const string idxSql = "CREATE INDEX IF NOT EXISTS idx_knowledge_query_hash ON knowledge_entries(query_hash)";
                using var idxCmd = new SQLiteCommand(idxSql, _connection);
                idxCmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration to add last_referenced and reference_count columns to knowledge_entries
        /// for attention decay scoring. Recently-queried knowledge ranks higher in injection.
        /// </summary>
        private void MigrateAddKnowledgeAttentionDecay()
        {
            const string checkSql = "PRAGMA table_info(knowledge_entries)";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            using var reader = checkCmd.ExecuteReader();
            bool hasLastReferenced = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "last_referenced", StringComparison.OrdinalIgnoreCase))
                { hasLastReferenced = true; break; }
            }
            reader.Close();

            if (!hasLastReferenced)
            {
                using var cmd1 = new SQLiteCommand(
                    "ALTER TABLE knowledge_entries ADD COLUMN last_referenced TEXT", _connection);
                cmd1.ExecuteNonQuery();

                using var cmd2 = new SQLiteCommand(
                    "ALTER TABLE knowledge_entries ADD COLUMN reference_count INTEGER NOT NULL DEFAULT 0", _connection);
                cmd2.ExecuteNonQuery();

                // Seed last_referenced from updated_at so existing entries aren't penalized
                using var seedCmd = new SQLiteCommand(
                    "UPDATE knowledge_entries SET last_referenced = updated_at WHERE last_referenced IS NULL", _connection);
                seedCmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration: add processing_status lifecycle tracking to session_lineage.
        /// Enables the session lifecycle state machine: open → closed → imported → indexed → complete.
        /// Also adds project_path for direct project lookups without parsing session_file_path.
        /// </summary>
        private void MigrateAddSessionLifecycleStatus()
        {
            const string checkSql = "PRAGMA table_info(session_lineage)";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            using var reader = checkCmd.ExecuteReader();
            bool hasProcessingStatus = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "processing_status", StringComparison.OrdinalIgnoreCase))
                { hasProcessingStatus = true; break; }
            }
            reader.Close();

            if (!hasProcessingStatus)
            {
                // Existing rows are already fully processed — default them to 'complete'
                using var cmd1 = new SQLiteCommand(
                    "ALTER TABLE session_lineage ADD COLUMN processing_status TEXT NOT NULL DEFAULT 'complete'", _connection);
                cmd1.ExecuteNonQuery();

                using var cmd2 = new SQLiteCommand(
                    "ALTER TABLE session_lineage ADD COLUMN project_path TEXT", _connection);
                cmd2.ExecuteNonQuery();

                using var cmd3 = new SQLiteCommand(
                    "ALTER TABLE session_lineage ADD COLUMN indexed_at TEXT", _connection);
                cmd3.ExecuteNonQuery();

                using var cmd4 = new SQLiteCommand(
                    "ALTER TABLE session_lineage ADD COLUMN summarized_at TEXT", _connection);
                cmd4.ExecuteNonQuery();

                // Add index for fast lookups by project + status
                using var idxCmd = new SQLiteCommand(
                    "CREATE INDEX IF NOT EXISTS idx_session_lineage_project_status ON session_lineage(project_path, processing_status)", _connection);
                idxCmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Migration: ensure the <c>task_worktrees</c> table exists for installs
        /// that predate Phase 1 worktree isolation. The same table is also created
        /// inline in <see cref="CreateSchema"/> for fresh installs; this safety
        /// net handles existing databases that miss the initial run.
        /// </summary>
        private void MigrateAddTaskWorktrees()
        {
            const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name='task_worktrees'";
            using var checkCmd = new SQLiteCommand(checkSql, _connection);
            var exists = checkCmd.ExecuteScalar();
            if (exists != null) return;

            const string createSql = @"
                CREATE TABLE IF NOT EXISTS task_worktrees (
                    task_id TEXT NOT NULL,
                    worktree_path TEXT NOT NULL,
                    branch_name TEXT NOT NULL,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    status TEXT NOT NULL DEFAULT 'active',
                    agent_name TEXT NOT NULL DEFAULT '__legacy__',
                    is_canonical INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (task_id, agent_name)
                );
                CREATE INDEX IF NOT EXISTS idx_task_worktrees_status ON task_worktrees(status);
                CREATE INDEX IF NOT EXISTS idx_task_worktrees_task ON task_worktrees(task_id);
            ";
            using var createCmd = new SQLiteCommand(createSql, _connection);
            createCmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Migration for per-agent worktree isolation (task bab81a92 / design ff1dc68f):
        /// adds the <c>agent_name</c> and <c>is_canonical</c> columns and widens the
        /// primary key from <c>(task_id)</c> to <c>(task_id, agent_name)</c> so multiple
        /// agents can each hold their own worktree on the same task. SQLite cannot
        /// redefine a primary key in place, so the table is rebuilt. Pre-existing rows
        /// (one worktree per task, implicitly owned by the task's assignee) backfill to
        /// <c>agent_name = tasks.assignee</c> (or <c>'__legacy__'</c> when unknown) and
        /// <c>is_canonical = 1</c>. Idempotent: a no-op once <c>agent_name</c> exists
        /// (already migrated, or a fresh DB whose CreateSchema declared the new layout).
        /// </summary>
        private void MigrateAddAgentToTaskWorktrees()
        {
            // Guard on the column rather than the table: the table always exists by now
            // (CreateSchema / MigrateAddTaskWorktrees), so we key off the new column.
            bool columnExists = false;
            using (var checkCmd = new SQLiteCommand("PRAGMA table_info(task_worktrees)", _connection))
            using (var reader = checkCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1).Equals("agent_name", StringComparison.OrdinalIgnoreCase))
                    {
                        columnExists = true;
                        break;
                    }
                }
            }

            if (columnExists) return;

            // Rebuild the table with the composite primary key, in a transaction so a
            // mid-rebuild failure leaves the original table intact.
            using var tx = _connection.BeginTransaction();
            const string rebuildSql = @"
                CREATE TABLE task_worktrees_new (
                    task_id TEXT NOT NULL,
                    worktree_path TEXT NOT NULL,
                    branch_name TEXT NOT NULL,
                    created_at DATETIME NOT NULL DEFAULT (datetime('now')),
                    status TEXT NOT NULL DEFAULT 'active',
                    agent_name TEXT NOT NULL DEFAULT '__legacy__',
                    is_canonical INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (task_id, agent_name)
                );
                INSERT INTO task_worktrees_new
                    (task_id, worktree_path, branch_name, created_at, status, agent_name, is_canonical)
                SELECT
                    task_id, worktree_path, branch_name, created_at, status,
                    COALESCE((SELECT assignee FROM tasks WHERE tasks.id = task_worktrees.task_id), '__legacy__'),
                    1
                FROM task_worktrees;
                DROP TABLE task_worktrees;
                ALTER TABLE task_worktrees_new RENAME TO task_worktrees;
                CREATE INDEX IF NOT EXISTS idx_task_worktrees_status ON task_worktrees(status);
                CREATE INDEX IF NOT EXISTS idx_task_worktrees_task ON task_worktrees(task_id);
            ";
            using (var cmd = new SQLiteCommand(rebuildSql, _connection, tx))
            {
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        #region Project Notes

        /// <summary>
        /// Gets the notes content for a project by its filesystem path.
        /// Returns empty string if no notes exist yet.
        /// </summary>
        public string GetProjectNotes(string projectPath)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(projectPath)) return "";
            const string sql = "SELECT content FROM project_notes WHERE project_path = @path";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@path", projectPath);
            var result = cmd.ExecuteScalar();
            return result?.ToString() ?? "";
        }

        /// <summary>
        /// Saves notes for a project (upsert). Creates the record if it doesn't exist.
        /// </summary>
        public void SaveProjectNotes(string projectPath, string content, string updatedBy = null)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(projectPath)) return;
            const string sql = @"
                INSERT INTO project_notes (id, project_path, content, updated_at, updated_by)
                VALUES (@id, @path, @content, datetime('now'), @updatedBy)
                ON CONFLICT(project_path) DO UPDATE SET
                    content = @content,
                    updated_at = datetime('now'),
                    updated_by = @updatedBy";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N").Substring(0, 8));
            cmd.Parameters.AddWithValue("@path", projectPath);
            cmd.Parameters.AddWithValue("@content", content ?? "");
            cmd.Parameters.AddWithValue("@updatedBy", (object)updatedBy ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // -- Project Note Tabs (multi-tab notes) --

        /// <summary>
        /// Get all note tabs for a project, ordered by tab_order.
        /// Auto-migrates from legacy project_notes if needed.
        /// </summary>
        public List<(string Name, string Content, bool IsDefault)> GetProjectNoteTabs(string projectPath)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(projectPath)) return new List<(string, string, bool)>();
            string rawPath = projectPath;                    // pre-normalization spelling (for legacy fallback)
            projectPath = NormalizeNoteTabPath(projectPath); // canonical key — stable across path-spelling

            var tabs = new List<(string Name, string Content, bool IsDefault)>();
            const string sql = "SELECT tab_name, content, is_default FROM project_note_tabs WHERE project_path = @path ORDER BY tab_order";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@path", projectPath);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tabs.Add((
                        reader.GetString(0),
                        reader.IsDBNull(1) ? "" : reader.GetString(1),
                        reader.GetInt32(2) == 1
                    ));
                }
            }

            // Self-heal: correctness must NOT depend on MigrateNormalizeNoteTabPaths
            // having run/succeeded. If the canonical key found nothing but rows still
            // exist under the raw (pre-normalization) spelling, adopt them under the
            // canonical key so this and every future read/write converge. Safe to re-key
            // here because the canonical lookup above returned zero rows (no tab-name
            // collision possible). Only on the cold path (no canonical rows).
            if (tabs.Count == 0 && !string.Equals(rawPath, projectPath, StringComparison.Ordinal))
            {
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@path", rawPath);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        tabs.Add((
                            reader.GetString(0),
                            reader.IsDBNull(1) ? "" : reader.GetString(1),
                            reader.GetInt32(2) == 1
                        ));
                    }
                }
                if (tabs.Count > 0)
                {
                    try
                    {
                        using var upd = new SQLiteCommand(
                            "UPDATE project_note_tabs SET project_path = @norm WHERE project_path = @raw", _connection);
                        upd.Parameters.AddWithValue("@norm", projectPath);
                        upd.Parameters.AddWithValue("@raw", rawPath);
                        upd.ExecuteNonQuery();
                    }
                    catch { /* re-key is best-effort; the rows are already returned correctly */ }
                    return tabs;
                }
            }

            if (tabs.Count == 0)
            {
                // Migrate from legacy single-note project_notes, or create an empty
                // default tab. project_notes is NOT path-normalized, so read under the
                // canonical key first, then fall back to the RAW spelling — otherwise a
                // legacy note saved under a non-canonical path (trailing slash, etc.)
                // would be silently lost and replaced by an empty "General" tab.
                string legacyContent = GetProjectNotes(projectPath);
                if (string.IsNullOrEmpty(legacyContent) &&
                    !string.Equals(rawPath, projectPath, StringComparison.Ordinal))
                {
                    legacyContent = GetProjectNotes(rawPath);
                }
                SaveNoteTab(projectPath, "General", legacyContent, 0, true);
                tabs.Add(("General", legacyContent, true));
            }

            return tabs;
        }

        /// <summary>
        /// Save/upsert a single note tab.
        /// </summary>
        public void SaveNoteTab(string projectPath, string tabName, string content, int? tabOrder = null, bool? isDefault = null)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(tabName)) return;
            projectPath = NormalizeNoteTabPath(projectPath); // canonical key — stable across path-spelling

            // Get current max tab_order if not specified
            if (!tabOrder.HasValue)
            {
                const string maxSql = "SELECT COALESCE(MAX(tab_order), -1) FROM project_note_tabs WHERE project_path = @path";
                using var maxCmd = new SQLiteCommand(maxSql, _connection);
                maxCmd.Parameters.AddWithValue("@path", projectPath);
                tabOrder = Convert.ToInt32(maxCmd.ExecuteScalar()) + 1;
            }

            const string sql = @"
                INSERT INTO project_note_tabs (id, project_path, tab_name, content, tab_order, is_default, created_at, updated_at)
                VALUES (@id, @path, @name, @content, @order, @isDefault, datetime('now'), datetime('now'))
                ON CONFLICT(project_path, tab_name) DO UPDATE SET
                    content = @content,
                    updated_at = datetime('now')";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N").Substring(0, 8));
            cmd.Parameters.AddWithValue("@path", projectPath);
            cmd.Parameters.AddWithValue("@name", tabName);
            cmd.Parameters.AddWithValue("@content", content ?? "");
            cmd.Parameters.AddWithValue("@order", tabOrder.Value);
            cmd.Parameters.AddWithValue("@isDefault", (isDefault ?? false) ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Delete a note tab (cannot delete default tab).
        /// </summary>
        public bool DeleteNoteTab(string projectPath, string tabName)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(tabName)) return false;
            projectPath = NormalizeNoteTabPath(projectPath); // canonical key — stable across path-spelling
            const string sql = "DELETE FROM project_note_tabs WHERE project_path = @path AND tab_name = @name AND is_default = 0";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@path", projectPath);
            cmd.Parameters.AddWithValue("@name", tabName);
            return cmd.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Rename a note tab.
        /// </summary>
        public bool RenameNoteTab(string projectPath, string oldName, string newName)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return false;
            projectPath = NormalizeNoteTabPath(projectPath); // canonical key — stable across path-spelling
            const string sql = "UPDATE project_note_tabs SET tab_name = @newName, updated_at = datetime('now') WHERE project_path = @path AND tab_name = @oldName";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@path", projectPath);
            cmd.Parameters.AddWithValue("@oldName", oldName);
            cmd.Parameters.AddWithValue("@newName", newName);
            return cmd.ExecuteNonQuery() > 0;
        }

        /// <summary>
        /// Reorder note tabs by setting tab_order based on the provided name sequence.
        /// </summary>
        public void ReorderNoteTabs(string projectPath, List<string> tabNames)
        {
            using var gate = LockConn();
            if (string.IsNullOrEmpty(projectPath) || tabNames == null) return;
            projectPath = NormalizeNoteTabPath(projectPath); // canonical key — stable across path-spelling
            const string sql = "UPDATE project_note_tabs SET tab_order = @order WHERE project_path = @path AND tab_name = @name";
            for (int i = 0; i < tabNames.Count; i++)
            {
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@path", projectPath);
                cmd.Parameters.AddWithValue("@name", tabNames[i]);
                cmd.Parameters.AddWithValue("@order", i);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// One-time cleanup of orphan note-tab rows that older terminals created by
        /// keying notes to a worktree path. Deletes only rows with EMPTY content whose
        /// (normalized) project_path is NOT a registered project. Preserves every
        /// registered-project row and every non-empty row (incl. notes someone actually
        /// typed inside a worktree terminal). Returns the number of rows deleted.
        /// </summary>
        public int PurgeOrphanEmptyNoteTabs(IEnumerable<string> registeredProjectPaths)
        {
            // Whole method serialized on _dbLock (runtime site: startup orphan cleanup).
            // Holding the gate across SELECT → build orphans → DELETE also keeps the
            // emptiness re-check atomic with the delete (reinforces the TOCTOU note below).
            using var gate = LockConn();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (registeredProjectPaths != null)
            {
                foreach (var p in registeredProjectPaths)
                {
                    string n = NormalizeNoteTabPath(p);
                    if (!string.IsNullOrEmpty(n)) keep.Add(n);
                }
            }

            // Collect empty-content rows whose path isn't a registered project.
            var orphans = new List<(string Path, string Name)>();
            const string selectSql = "SELECT project_path, tab_name FROM project_note_tabs WHERE LENGTH(COALESCE(content,'')) = 0";
            using (var sel = new SQLiteCommand(selectSql, _connection))
            using (var reader = sel.ExecuteReader())
            {
                while (reader.Read())
                {
                    string path = reader.IsDBNull(0) ? null : reader.GetString(0);
                    string name = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (string.IsNullOrEmpty(path) || name == null) continue;
                    if (keep.Contains(NormalizeNoteTabPath(path))) continue; // registered → preserve
                    // Provenance guard: only purge rows that actually came from a git
                    // worktree (the orphan source this cleanup targets). An unregistered
                    // AD-HOC folder is legitimately supported by the HUD (Notes key to the
                    // raw working dir), and its empty/named tabs are real user state — never
                    // delete those just because the folder isn't a registered project.
                    if (!LooksLikeWorktreePath(path)) continue;
                    orphans.Add((path, name));
                }
            }

            if (orphans.Count == 0) return 0;

            int deleted = 0;
            // Re-check emptiness IN the DELETE (not just the earlier SELECT) so a row
            // that gained content between selection and deletion is never removed —
            // closes the select-then-delete TOCTOU window.
            const string delSql = "DELETE FROM project_note_tabs WHERE project_path = @path AND tab_name = @name AND LENGTH(COALESCE(content,'')) = 0";
            using (var tx = _connection.BeginTransaction())
            {
                foreach (var (path, name) in orphans)
                {
                    using var cmd = new SQLiteCommand(delSql, _connection, tx);
                    cmd.Parameters.AddWithValue("@path", path);
                    cmd.Parameters.AddWithValue("@name", name);
                    deleted += cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return deleted;
        }

        /// <summary>
        /// The authoritative canonical form of a note-tab project_path key. Every
        /// project_note_tabs read/write normalizes through this, and the one-time
        /// MigrateNormalizeNoteTabPaths migration rewrites existing rows to it, so note
        /// identity is STABLE across the common path-spelling differences: full path
        /// (collapses "/" vs "\\" and "." / ".."), trailing separators trimmed. Without
        /// this, a later edit to a project's path/source_path spelling would strand its
        /// notes behind an exact project_path match. Used identically by the orphan
        /// purge so its "registered?" check lines up with how rows are keyed.
        ///
        /// Deliberately does NOT case-fold. Lower-casing the persisted key would be
        /// unsafe on case-SENSITIVE path namespaces (\\wsl$ shares, opt-in
        /// case-sensitive NTFS directories), where "Foo" and "foo" are genuinely
        /// different folders — folding them would merge two distinct projects' notes
        /// during migration (cross-project bleed). Casing-only path edits are rare and,
        /// on the default case-insensitive Windows FS, equivalent spellings already
        /// share storage; the small residual (a manual case change re-keying notes) is
        /// strictly safer than risking note cross-contamination. Comparisons that DO
        /// want case-insensitivity (purge keep-set, folder match) apply
        /// OrdinalIgnoreCase at the comparison site, not in the stored key.
        /// </summary>
        private static string NormalizeNoteTabPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch { return path.TrimEnd('\\', '/'); }
        }

        /// <summary>
        /// True when a path matches a MultiTerminal git-worktree layout — the only kind
        /// of orphan note-tab row the startup purge is allowed to delete. MT worktrees
        /// live under three historical layouts, each a "worktrees" directory followed by
        /// an 8-hex task-id segment: "&lt;repo&gt;\.claude\worktrees\&lt;id&gt;",
        /// "&lt;repo&gt;\worktrees\&lt;id&gt;", and "&lt;repo&gt;-worktrees\&lt;id&gt;".
        /// We require BOTH a separator-bounded "worktrees" (or "*-worktrees") directory
        /// AND a following 8-hex id segment, so a legitimate ad-hoc folder that merely
        /// contains a "worktrees" directory (e.g. "D:\clients\worktrees\demo") is NOT
        /// classified as an orphan — a plain substring match would wrongly delete its
        /// empty tabs.
        /// </summary>
        private static bool LooksLikeWorktreePath(string path)
            => WorktreeLayout.LooksLikeWorktreePath(path);

        /// <summary>
        /// One-time migration: rewrite every project_note_tabs.project_path to its
        /// canonical form (<see cref="NormalizeNoteTabPath"/>) so pre-existing rows keep
        /// matching now that all note reads/writes normalize the key. Idempotent
        /// (re-running finds nothing to change). Runs in a single transaction.
        ///
        /// Collisions — two spellings of the same folder carrying the same tab name —
        /// are merged NON-destructively in this priority: drop an empty/identical
        /// duplicate (no content lost); if the canonical target is empty but this row
        /// has content, the content wins; if both differ and are non-empty, BOTH are
        /// preserved (this row moves under a deduped tab name). Content is never deleted.
        /// </summary>
        private void MigrateNormalizeNoteTabPaths()
        {
            try
            {
                var rows = new List<(string Id, string Path, string Tab, string Content)>();
                using (var sel = new SQLiteCommand(
                    "SELECT id, project_path, tab_name, COALESCE(content,'') FROM project_note_tabs", _connection))
                using (var r = sel.ExecuteReader())
                {
                    while (r.Read())
                        rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
                }
                if (rows.Count == 0) return;

                using var tx = _connection.BeginTransaction();
                foreach (var row in rows)
                {
                    string canon = NormalizeNoteTabPath(row.Path);
                    if (string.IsNullOrEmpty(canon) || string.Equals(canon, row.Path, StringComparison.Ordinal))
                        continue; // already canonical

                    // Is there a DIFFERENT row already at the canonical (path, tab)?
                    string targetContent = null;
                    using (var chk = new SQLiteCommand(
                        "SELECT COALESCE(content,'') FROM project_note_tabs WHERE project_path=@c AND tab_name=@t AND id<>@id",
                        _connection, tx))
                    {
                        chk.Parameters.AddWithValue("@c", canon);
                        chk.Parameters.AddWithValue("@t", row.Tab);
                        chk.Parameters.AddWithValue("@id", row.Id);
                        var o = chk.ExecuteScalar();
                        if (o != null && o != DBNull.Value) targetContent = (string)o;
                    }

                    if (targetContent == null)
                    {
                        // No collision — just re-key this row.
                        using var upd = new SQLiteCommand(
                            "UPDATE project_note_tabs SET project_path=@c WHERE id=@id", _connection, tx);
                        upd.Parameters.AddWithValue("@c", canon);
                        upd.Parameters.AddWithValue("@id", row.Id);
                        upd.ExecuteNonQuery();
                    }
                    else if (string.IsNullOrEmpty(row.Content) ||
                             string.Equals(row.Content, targetContent, StringComparison.Ordinal))
                    {
                        // This row is empty or a duplicate of the target — drop it (target keeps the content).
                        using var del = new SQLiteCommand(
                            "DELETE FROM project_note_tabs WHERE id=@id", _connection, tx);
                        del.Parameters.AddWithValue("@id", row.Id);
                        del.ExecuteNonQuery();
                    }
                    else if (string.IsNullOrEmpty(targetContent))
                    {
                        // Target is an empty placeholder; this row has real content — content wins.
                        using (var delT = new SQLiteCommand(
                            "DELETE FROM project_note_tabs WHERE project_path=@c AND tab_name=@t AND id<>@id", _connection, tx))
                        {
                            delT.Parameters.AddWithValue("@c", canon);
                            delT.Parameters.AddWithValue("@t", row.Tab);
                            delT.Parameters.AddWithValue("@id", row.Id);
                            delT.ExecuteNonQuery();
                        }
                        using var upd = new SQLiteCommand(
                            "UPDATE project_note_tabs SET project_path=@c WHERE id=@id", _connection, tx);
                        upd.Parameters.AddWithValue("@c", canon);
                        upd.Parameters.AddWithValue("@id", row.Id);
                        upd.ExecuteNonQuery();
                    }
                    else
                    {
                        // Both non-empty and different — preserve both under a deduped tab name.
                        string dedup = UniqueNoteTabName(tx, canon, row.Tab);
                        using var upd = new SQLiteCommand(
                            "UPDATE project_note_tabs SET project_path=@c, tab_name=@n WHERE id=@id", _connection, tx);
                        upd.Parameters.AddWithValue("@c", canon);
                        upd.Parameters.AddWithValue("@n", dedup);
                        upd.Parameters.AddWithValue("@id", row.Id);
                        upd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskDatabase] note-tab path normalization migration failed: {ex.Message}");
            }
        }

        /// <summary>Finds a tab name not already used under <paramref name="canonPath"/> by appending " (n)".</summary>
        private string UniqueNoteTabName(SQLiteTransaction tx, string canonPath, string baseName)
        {
            for (int n = 2; ; n++)
            {
                string candidate = $"{baseName} ({n})";
                using var cmd = new SQLiteCommand(
                    "SELECT 1 FROM project_note_tabs WHERE project_path=@c AND tab_name=@t LIMIT 1", _connection, tx);
                cmd.Parameters.AddWithValue("@c", canonPath);
                cmd.Parameters.AddWithValue("@t", candidate);
                if (cmd.ExecuteScalar() == null) return candidate;
                if (n > 1000) return $"{baseName} ({Guid.NewGuid().ToString("N").Substring(0, 6)})";
            }
        }

        #endregion

        #region Message Images

        /// <summary>
        /// Save a batch of message images. Returns the batch ID.
        /// </summary>
        public string SaveMessageImages(List<MessageImageInput> images)
        {
            using var gate = LockConn();
            string batchId = Guid.NewGuid().ToString("N").Substring(0, 12);

            const string sql = @"
                INSERT INTO message_images (id, batch_id, file_name, mime_type, image_data, file_size_bytes, created_at)
                VALUES (@id, @batchId, @fileName, @mimeType, @imageData, @fileSizeBytes, datetime('now'))";

            foreach (var image in images)
            {
                string id = Guid.NewGuid().ToString("N").Substring(0, 12);
                using var cmd = new SQLiteCommand(sql, _connection);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@batchId", batchId);
                cmd.Parameters.AddWithValue("@fileName", image.FileName);
                cmd.Parameters.AddWithValue("@mimeType", image.MimeType);
                cmd.Parameters.AddWithValue("@imageData", image.Base64Data);
                cmd.Parameters.AddWithValue("@fileSizeBytes", image.FileSizeBytes);
                cmd.ExecuteNonQuery();
            }

            return batchId;
        }

        /// <summary>
        /// Get all images in a batch by batch ID.
        /// </summary>
        public List<MessageImage> GetMessageImages(string batchId)
        {
            using var gate = LockConn();
            var images = new List<MessageImage>();

            const string sql = @"
                SELECT id, batch_id, file_name, mime_type, image_data, file_size_bytes, created_at
                FROM message_images
                WHERE batch_id = @batchId
                ORDER BY created_at ASC";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@batchId", batchId);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                images.Add(new MessageImage
                {
                    Id = reader.GetString(0),
                    BatchId = reader.GetString(1),
                    FileName = reader.GetString(2),
                    MimeType = reader.GetString(3),
                    Base64Data = reader.GetString(4),
                    FileSizeBytes = reader.GetInt32(5),
                    CreatedAt = reader.GetDateTime(6)
                });
            }

            return images;
        }

        /// <summary>
        /// Delete all images in a batch.
        /// </summary>
        public bool DeleteMessageImageBatch(string batchId)
        {
            using var gate = LockConn();
            const string sql = "DELETE FROM message_images WHERE batch_id = @batchId";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@batchId", batchId);
            return cmd.ExecuteNonQuery() > 0;
        }

        #endregion

        #region Notification Events

        public string SaveNotificationEvent(string notificationType, string title, string message,
            string sessionId, string agentName, string cwd)
        {
            using var gate = LockConn();
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            const string sql = @"
                INSERT INTO notification_events (id, notification_type, title, message, session_id, agent_name, cwd, created_at)
                VALUES (@id, @type, @title, @message, @sessionId, @agentName, @cwd, datetime('now'))";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@type", notificationType);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@message", (object)message ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sessionId", (object)sessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@agentName", (object)agentName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cwd", (object)cwd ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return id;
        }

        public List<Dictionary<string, object>> GetNotificationEvents(int limit = 50, bool unreadOnly = false)
        {
            using var gate = LockConn();
            string sql = unreadOnly
                ? "SELECT * FROM notification_events WHERE read_at IS NULL ORDER BY created_at DESC LIMIT @limit"
                : "SELECT * FROM notification_events ORDER BY created_at DESC LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<Dictionary<string, object>>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }
            return results;
        }

        public void MarkNotificationRead(string id)
        {
            using var gate = LockConn();
            const string sql = "UPDATE notification_events SET read_at = datetime('now') WHERE id = @id";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        public int GetUnreadNotificationCount()
        {
            using var gate = LockConn();
            const string sql = "SELECT COUNT(*) FROM notification_events WHERE read_at IS NULL";
            using var cmd = new SQLiteCommand(sql, _connection);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        #endregion

        #region Agent Invocations

        public void SaveAgentInvocation(string id, string agentName, string taskId, string invokedBy,
            string modelUsed, string verdict, int? score, int findingsCount, long? durationMs,
            DateTime invokedAt, DateTime? completedAt, string reportSummary)
        {
            using var gate = LockConn();
            const string sql = @"
                INSERT INTO agent_invocations (id, agent_name, task_id, invoked_by, model_used, verdict, score, findings_count, duration_ms, invoked_at, completed_at, report_summary)
                VALUES (@id, @agentName, @taskId, @invokedBy, @modelUsed, @verdict, @score, @findingsCount, @durationMs, @invokedAt, @completedAt, @reportSummary)
                ON CONFLICT(id) DO UPDATE SET
                    verdict = @verdict,
                    score = @score,
                    findings_count = @findingsCount,
                    duration_ms = @durationMs,
                    completed_at = @completedAt,
                    report_summary = @reportSummary";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@agentName", agentName);
            cmd.Parameters.AddWithValue("@taskId", (object)taskId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@invokedBy", (object)invokedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@modelUsed", (object)modelUsed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@verdict", (object)verdict ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@score", score.HasValue ? (object)score.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@findingsCount", findingsCount);
            cmd.Parameters.AddWithValue("@durationMs", durationMs.HasValue ? (object)durationMs.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@invokedAt", invokedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@completedAt", completedAt.HasValue ? (object)completedAt.Value.ToString("o") : DBNull.Value);
            cmd.Parameters.AddWithValue("@reportSummary", (object)reportSummary ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        public List<Dictionary<string, object>> GetAgentInvocations(string agentName = null, string taskId = null, int limit = 50)
        {
            using var gate = LockConn();
            var results = new List<Dictionary<string, object>>();
            var conditions = new List<string>();
            if (!string.IsNullOrEmpty(agentName))
                conditions.Add("agent_name = @agentName");
            if (!string.IsNullOrEmpty(taskId))
                conditions.Add("task_id = @taskId");

            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            var sql = $"SELECT * FROM agent_invocations {where} ORDER BY invoked_at DESC LIMIT @limit";

            // CA2100: WHERE fragments are compile-time-constant strings; all user input flows through parameters.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
            if (!string.IsNullOrEmpty(agentName))
                cmd.Parameters.AddWithValue("@agentName", agentName);
            if (!string.IsNullOrEmpty(taskId))
                cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }
            return results;
        }

        public List<Dictionary<string, object>> GetAgentStats()
        {
            using var gate = LockConn();
            var results = new List<Dictionary<string, object>>();
            const string sql = @"
                SELECT
                    agent_name,
                    COUNT(*) as total_invocations,
                    COUNT(CASE WHEN verdict IN ('PASS', 'PROCEED', 'SHIP IT') THEN 1 END) as pass_count,
                    COUNT(CASE WHEN verdict IN ('FAIL', 'BLOCK', 'REWORK', 'RETHINK') THEN 1 END) as fail_count,
                    COUNT(CASE WHEN verdict IN ('PASS WITH NOTES', 'PASS WITH WARNINGS', 'REVISE', 'FIX AND RE-RUN') THEN 1 END) as warn_count,
                    ROUND(AVG(score), 1) as avg_score,
                    SUM(findings_count) as total_findings,
                    ROUND(AVG(duration_ms), 0) as avg_duration_ms,
                    MAX(invoked_at) as last_invoked
                FROM agent_invocations
                GROUP BY agent_name
                ORDER BY total_invocations DESC";

            using var cmd = new SQLiteCommand(sql, _connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }
            return results;
        }

        #endregion

        #region Task Reports

        /// <summary>
        /// Saves an agent report linked to a task.
        /// </summary>
        public void SaveTaskReport(string id, string taskId, string invocationId, string agentName,
            string reportType, string reportContent, string verdict, int? score, string createdBy)
        {
            using var gate = LockConn();
            const string sql = @"
                INSERT INTO task_reports (id, task_id, invocation_id, agent_name, report_type, report_content, verdict, score, created_by)
                VALUES (@id, @taskId, @invocationId, @agentName, @reportType, @reportContent, @verdict, @score, @createdBy)
                ON CONFLICT(id) DO UPDATE SET
                    report_content = @reportContent,
                    verdict = @verdict,
                    score = @score";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@invocationId", (object)invocationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@agentName", agentName);
            cmd.Parameters.AddWithValue("@reportType", reportType ?? "html");
            cmd.Parameters.AddWithValue("@reportContent", reportContent);
            cmd.Parameters.AddWithValue("@verdict", (object)verdict ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@score", score.HasValue ? (object)score.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@createdBy", (object)createdBy ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns reports for a given task, newest first.
        /// </summary>
        public List<Dictionary<string, object>> GetTaskReports(string taskId, string agentName = null, int limit = 50)
        {
            using var gate = LockConn();
            var results = new List<Dictionary<string, object>>();
            var conditions = new List<string> { "task_id = @taskId" };
            if (!string.IsNullOrEmpty(agentName))
                conditions.Add("agent_name = @agentName");

            var where = "WHERE " + string.Join(" AND ", conditions);
            var sql = $"SELECT id, task_id, invocation_id, agent_name, report_type, verdict, score, created_at, created_by FROM task_reports {where} ORDER BY created_at DESC LIMIT @limit";

            // CA2100: WHERE fragments are compile-time-constant strings; all user input flows through parameters.
#pragma warning disable CA2100
            using var cmd = new SQLiteCommand(sql, _connection);
#pragma warning restore CA2100
            cmd.Parameters.AddWithValue("@taskId", taskId);
            if (!string.IsNullOrEmpty(agentName))
                cmd.Parameters.AddWithValue("@agentName", agentName);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                results.Add(row);
            }
            return results;
        }

        /// <summary>
        /// Returns the full report content for a specific report ID.
        /// </summary>
        public Dictionary<string, object> GetTaskReport(string reportId)
        {
            using var gate = LockConn();
            const string sql = "SELECT * FROM task_reports WHERE id = @id";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@id", reportId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                return row;
            }
            return null;
        }

        /// <summary>
        /// Returns the count of reports for a given task.
        /// </summary>
        public int GetTaskReportCount(string taskId)
        {
            using var gate = LockConn();
            const string sql = "SELECT COUNT(*) FROM task_reports WHERE task_id = @taskId";
            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@taskId", taskId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        #endregion

        // The Connection property was DELETED in ticket bb2b0104. Exposing the raw handle let
        // sibling classes run ungated commands on TaskDatabase's connection — the cross-class
        // race this program set out to kill. The invariant now: every SQLite connection has
        // exactly ONE owner class; each owner opens its OWN connection via MultiterminalDb.Open()
        // and serializes its own access. No class touches another's handle. If a new feature needs
        // multiterminal.db, it opens its own connection through the factory — it does NOT get a
        // handle from here. The verifier (scripts/verify-taskdb-gate.mjs, ALLOW_CONNECTION_EXPOSURE=false)
        // fails the build if a Connection property reappears on any connection-owning class.

        #endregion

        #region Chat Messages

        /// <summary>
        /// Saves an inter-terminal chat message.
        /// </summary>
        public void SaveChatMessage(string messageId, string fromTerminal, string toTerminal, string content, DateTime timestamp, bool isBroadcast)
        {
            using var gate = LockConn();
            const string sql = @"
                INSERT OR IGNORE INTO chat_messages (message_id, from_terminal, to_terminal, content, timestamp, is_broadcast)
                VALUES (@messageId, @from, @to, @content, @timestamp, @isBroadcast)";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@messageId", messageId);
            cmd.Parameters.AddWithValue("@from", fromTerminal);
            cmd.Parameters.AddWithValue("@to", toTerminal);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@timestamp", timestamp.ToString("o"));
            cmd.Parameters.AddWithValue("@isBroadcast", isBroadcast ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Returns chat messages, newest first.
        /// </summary>
        public List<ChatMessageRecord> GetChatMessages(int limit = 1000)
        {
            using var gate = LockConn();
            var results = new List<ChatMessageRecord>();
            string sql = "SELECT message_id, from_terminal, to_terminal, content, timestamp, is_broadcast FROM chat_messages ORDER BY timestamp DESC LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ChatMessageRecord
                {
                    MessageId = reader.GetString(0),
                    FromTerminal = reader.GetString(1),
                    ToTerminal = reader.GetString(2),
                    Content = reader.GetString(3),
                    Timestamp = DateTime.TryParse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTime.MinValue,
                    IsBroadcast = reader.GetInt32(5) != 0
                });
            }
            return results;
        }

        #endregion

        #region Project Session Queries

        /// <summary>
        /// Returns sessions whose file path starts with the given Claude project folder prefix.
        /// Used by ProjectPanel to show sessions for a specific project.
        /// </summary>
        public List<SessionLineageRecord> GetSessionsByFolder(string claudeProjectFolder, int limit = 20)
        {
            using var gate = LockConn();
            var results = new List<SessionLineageRecord>();
            string folderPrefix = claudeProjectFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            const string sql = @"
                SELECT id, session_id, parent_session_id, task_id, agent_name, session_type,
                       summary, session_file_path, started_at, ended_at, created_at
                FROM session_lineage
                WHERE session_file_path LIKE @prefix
                  AND session_type = 'terminal'
                ORDER BY started_at DESC
                LIMIT @limit";

            using var cmd = new SQLiteCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@prefix", folderPrefix + "%");
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSessionLineageFromReader(reader));
            }
            return results;
        }

        #endregion

        // CA1063: full Dispose(bool) template. Other IDisposable services in this codebase still
        // use the simpler one-method form pending a bulk sweep — see follow-up task f74c0ab0.
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
    /// Represents an inter-terminal chat message record from the database.
    /// </summary>
    public class ChatMessageRecord
    {
        public string MessageId { get; set; }
        public string FromTerminal { get; set; }
        public string ToTerminal { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsBroadcast { get; set; }
    }

    /// <summary>
    /// Represents a message search result with session context.
    /// </summary>
    public class MessageSearchResult
    {
        public string SessionId { get; set; }
        public string SessionSummary { get; set; }
        public long MessageId { get; set; }
        public string Role { get; set; }
        public string ContentSnippet { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
