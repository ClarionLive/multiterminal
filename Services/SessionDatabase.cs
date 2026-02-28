using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;

namespace MultiTerminal.Services
{
    #region Model Classes

    /// <summary>
    /// Represents a Claude Code session.
    /// </summary>
    public class Session
    {
        public long Id { get; set; }
        public string SessionId { get; set; }
        public string ProjectPath { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string InitialPrompt { get; set; }
        public string Summary { get; set; }
        public int TotalMessages { get; set; }
        public int TotalToolCalls { get; set; }
        public decimal TotalCost { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    /// <summary>
    /// Represents a message in a session.
    /// </summary>
    public class SessionMessage
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public int? TokenCount { get; set; }
        public string MessageType { get; set; }
    }

    /// <summary>
    /// Represents a tool use/call in a session.
    /// </summary>
    public class ToolUse
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public long? MessageId { get; set; }
        public string ToolName { get; set; }
        public string Input { get; set; }
        public string Output { get; set; }
        public DateTime Timestamp { get; set; }
        public int? DurationMs { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Represents a file reference in a session.
    /// </summary>
    public class FileRef
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public long? ToolUseId { get; set; }
        public string FilePath { get; set; }
        public string Operation { get; set; }
        public DateTime Timestamp { get; set; }
        public int? LinesChanged { get; set; }
    }

    /// <summary>
    /// Represents an error that occurred during a session.
    /// </summary>
    public class SessionError
    {
        public long Id { get; set; }
        public long SessionId { get; set; }
        public long? MessageId { get; set; }
        public long? ToolUseId { get; set; }
        public string ErrorType { get; set; }
        public string ErrorMessage { get; set; }
        public string StackTrace { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Represents a chat message from inter-terminal MCP communication.
    /// </summary>
    public class ChatMessage
    {
        public long Id { get; set; }
        public string MessageId { get; set; }
        public string FromTerminal { get; set; }
        public string ToTerminal { get; set; }
        public string Content { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsBroadcast { get; set; }
    }

    /// <summary>
    /// Represents a LEARNED message from the Pool Coordinator.
    /// Used for semantic memory across sessions.
    /// </summary>
    public class LearnedMessage
    {
        public long Id { get; set; }
        public string MessageId { get; set; }
        public string Instance { get; set; }
        public string Topic { get; set; }
        public string Summary { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Represents a terminal identity mapping to a Claude session.
    /// Used to track which Claude session belongs to which named terminal (Alice, Bob, etc.)
    /// </summary>
    public class TerminalIdentity
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string ProjectHash { get; set; }
        public string ProjectPath { get; set; }
        public string SessionId { get; set; }
        public string FirstPrompt { get; set; }
        public DateTime LastActive { get; set; }
    }

    /// <summary>
    /// Search result with relevance ranking.
    /// </summary>
    public class SearchResult
    {
        public string TableName { get; set; }
        public long RecordId { get; set; }
        public string MatchedText { get; set; }
        public double Rank { get; set; }
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

    #endregion

    /// <summary>
    /// SQLite database service for storing and searching Claude Code session history.
    /// Database is stored at %APPDATA%\multiterminal\sessions.db (centralized) or optionally at [projectPath]/.claude/sessions.db (legacy per-project)
    /// </summary>
    public class SessionDatabase : ISessionDatabase, IDisposable
    {
        private readonly string _projectPath;
        private readonly string _databasePath;
        private SQLiteConnection _connection;
        private bool _isDisposed;
        private bool _ftsAvailable;

        /// <summary>
        /// Gets the path to the centralized database location.
        /// </summary>
        /// <returns>Path to %APPDATA%\multiterminal\sessions.db</returns>
        public static string GetCentralizedDatabasePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "multiterminal");
            return Path.Combine(folder, "sessions.db");
        }

        /// <summary>
        /// Creates a new SessionDatabase using the centralized database location.
        /// Database is stored at %APPDATA%\multiterminal\sessions.db
        /// </summary>
        public SessionDatabase()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string multiTerminalFolder = Path.Combine(appData, "multiterminal");

            if (!Directory.Exists(multiTerminalFolder))
                Directory.CreateDirectory(multiTerminalFolder);

            _databasePath = Path.Combine(multiTerminalFolder, "sessions.db");
            _projectPath = null;
            Initialize();
        }

        /// <summary>
        /// Creates a new SessionDatabase for the specified project.
        /// </summary>
        /// <param name="projectPath">Path to the project folder.</param>
        public SessionDatabase(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                throw new ArgumentException("Project path is required", nameof(projectPath));

            _projectPath = projectPath;
            string claudeFolder = Path.Combine(projectPath, ".claude");

            if (!Directory.Exists(claudeFolder))
                Directory.CreateDirectory(claudeFolder);

            _databasePath = Path.Combine(claudeFolder, "sessions.db");
            Initialize();
        }

        /// <summary>
        /// Gets the path to the database file.
        /// </summary>
        public string DatabasePath => _databasePath;

        /// <summary>
        /// Gets whether the database connection is open.
        /// </summary>
        public bool IsConnected => _connection != null && _connection.State == ConnectionState.Open;

        #region Initialization

        private void Initialize()
        {
            System.Diagnostics.Trace.WriteLine($"[SessionDatabase] Initialize started, path: {_databasePath}");
            bool isNewDatabase = !File.Exists(_databasePath);
            System.Diagnostics.Trace.WriteLine($"[SessionDatabase] Is new database: {isNewDatabase}");

            string connectionString = $"Data Source={_databasePath};Version=3;";
            _connection = new SQLiteConnection(connectionString);
            System.Diagnostics.Trace.WriteLine("[SessionDatabase] Opening connection...");
            _connection.Open();
            System.Diagnostics.Trace.WriteLine("[SessionDatabase] Connection opened");

            // Enable WAL mode for better concurrency - allows MCP server and MultiTerminal
            // to access the database simultaneously without "readonly database" errors
            System.Diagnostics.Trace.WriteLine("[SessionDatabase] Enabling WAL mode...");
            ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            System.Diagnostics.Trace.WriteLine("[SessionDatabase] WAL mode enabled");

            if (isNewDatabase)
            {
                System.Diagnostics.Trace.WriteLine("[SessionDatabase] Initializing new database schema...");
                InitializeSchema();
                System.Diagnostics.Trace.WriteLine("[SessionDatabase] Schema initialized");
            }
            else
            {
                System.Diagnostics.Trace.WriteLine("[SessionDatabase] Upgrading schema if needed...");
                // Ensure schema is up to date
                UpgradeSchemaIfNeeded();
                System.Diagnostics.Trace.WriteLine("[SessionDatabase] Schema upgrade completed");
            }
            System.Diagnostics.Trace.WriteLine("[SessionDatabase] Initialize completed");
        }

        private void InitializeSchema()
        {
            // Transaction 1: Core tables (must succeed)
            using (var transaction = _connection.BeginTransaction())
            {
                try
                {
                    // Create sessions table
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS sessions (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            session_id TEXT UNIQUE NOT NULL,
                            project_path TEXT NOT NULL,
                            started_at TEXT NOT NULL,
                            ended_at TEXT,
                            initial_prompt TEXT,
                            summary TEXT,
                            total_messages INTEGER DEFAULT 0,
                            total_tool_calls INTEGER DEFAULT 0,
                            total_cost REAL DEFAULT 0,
                            input_tokens INTEGER DEFAULT 0,
                            output_tokens INTEGER DEFAULT 0,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP
                        )");

                    // Create messages table
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS messages (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            session_id INTEGER NOT NULL,
                            role TEXT NOT NULL,
                            content TEXT,
                            timestamp TEXT NOT NULL,
                            token_count INTEGER,
                            message_type TEXT,
                            FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
                        )");

                    // Create tool_uses table
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS tool_uses (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            session_id INTEGER NOT NULL,
                            message_id INTEGER,
                            tool_name TEXT NOT NULL,
                            input TEXT,
                            output TEXT,
                            timestamp TEXT NOT NULL,
                            duration_ms INTEGER,
                            success INTEGER DEFAULT 1,
                            error_message TEXT,
                            FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE,
                            FOREIGN KEY (message_id) REFERENCES messages(id) ON DELETE SET NULL
                        )");

                    // Create file_refs table
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS file_refs (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            session_id INTEGER NOT NULL,
                            tool_use_id INTEGER,
                            file_path TEXT NOT NULL,
                            operation TEXT NOT NULL,
                            timestamp TEXT NOT NULL,
                            lines_changed INTEGER,
                            FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE,
                            FOREIGN KEY (tool_use_id) REFERENCES tool_uses(id) ON DELETE SET NULL
                        )");

                    // Create errors table
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS errors (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            session_id INTEGER NOT NULL,
                            message_id INTEGER,
                            tool_use_id INTEGER,
                            error_type TEXT,
                            error_message TEXT,
                            stack_trace TEXT,
                            timestamp TEXT NOT NULL,
                            FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE,
                            FOREIGN KEY (message_id) REFERENCES messages(id) ON DELETE SET NULL,
                            FOREIGN KEY (tool_use_id) REFERENCES tool_uses(id) ON DELETE SET NULL
                        )");

                    // Create chat_messages table for inter-terminal MCP chat
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS chat_messages (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            message_id TEXT UNIQUE NOT NULL,
                            from_terminal TEXT NOT NULL,
                            to_terminal TEXT NOT NULL,
                            content TEXT NOT NULL,
                            timestamp TEXT NOT NULL,
                            is_broadcast INTEGER DEFAULT 0,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP
                        )");

                    // Create terminal_identities table for session identity persistence
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS terminal_identities (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            name TEXT NOT NULL,
                            project_hash TEXT NOT NULL,
                            project_path TEXT,
                            session_id TEXT NOT NULL,
                            first_prompt TEXT,
                            last_active TEXT NOT NULL,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                            UNIQUE(name, project_hash)
                        )");

                    // Create learned_messages table for LEARNED pool messages (semantic memory)
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS learned_messages (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            message_id TEXT UNIQUE NOT NULL,
                            instance TEXT NOT NULL,
                            topic TEXT NOT NULL,
                            summary TEXT,
                            tags TEXT,
                            timestamp TEXT NOT NULL,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP
                        )");

                    // Create indexes for common queries
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_sessions_started_at ON sessions(started_at DESC)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_sessions_project_path ON sessions(project_path)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_messages_session_id ON messages(session_id)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_messages_timestamp ON messages(timestamp)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_tool_uses_session_id ON tool_uses(session_id)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_tool_uses_tool_name ON tool_uses(tool_name)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_file_refs_session_id ON file_refs(session_id)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_file_refs_file_path ON file_refs(file_path)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_errors_session_id ON errors(session_id)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_chat_messages_timestamp ON chat_messages(timestamp)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_chat_messages_terminals ON chat_messages(from_terminal, to_terminal)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_terminal_identities_name ON terminal_identities(name)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_terminal_identities_project ON terminal_identities(project_hash)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_learned_messages_timestamp ON learned_messages(timestamp)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_learned_messages_instance ON learned_messages(instance)");

                    // Store schema version
                    ExecuteNonQuery("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY)");
                    ExecuteNonQuery("INSERT OR REPLACE INTO schema_version (version) VALUES (4)");

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            // Transaction 2: FTS tables (optional - gracefully degrade if FTS5 not available)
            InitializeFtsSchema();
        }

        private void InitializeFtsSchema()
        {
            try
            {
                using (var transaction = _connection.BeginTransaction())
                {
                    // Create FTS5 virtual tables for full-text search
                    ExecuteNonQuery(@"
                        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                            content,
                            content='messages',
                            content_rowid='id'
                        )");

                    ExecuteNonQuery(@"
                        CREATE VIRTUAL TABLE IF NOT EXISTS tool_uses_fts USING fts5(
                            tool_name,
                            input,
                            output,
                            content='tool_uses',
                            content_rowid='id'
                        )");

                    // Create triggers to keep FTS tables in sync
                    ExecuteNonQuery(@"
                        CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
                            INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
                        END");

                    ExecuteNonQuery(@"
                        CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
                            INSERT INTO messages_fts(messages_fts, rowid, content) VALUES('delete', old.id, old.content);
                        END");

                    ExecuteNonQuery(@"
                        CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
                            INSERT INTO messages_fts(messages_fts, rowid, content) VALUES('delete', old.id, old.content);
                            INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
                        END");

                    ExecuteNonQuery(@"
                        CREATE TRIGGER IF NOT EXISTS tool_uses_ai AFTER INSERT ON tool_uses BEGIN
                            INSERT INTO tool_uses_fts(rowid, tool_name, input, output) VALUES (new.id, new.tool_name, new.input, new.output);
                        END");

                    ExecuteNonQuery(@"
                        CREATE TRIGGER IF NOT EXISTS tool_uses_ad AFTER DELETE ON tool_uses BEGIN
                            INSERT INTO tool_uses_fts(tool_uses_fts, rowid, tool_name, input, output) VALUES('delete', old.id, old.tool_name, old.input, old.output);
                        END");

                    ExecuteNonQuery(@"
                        CREATE TRIGGER IF NOT EXISTS tool_uses_au AFTER UPDATE ON tool_uses BEGIN
                            INSERT INTO tool_uses_fts(tool_uses_fts, rowid, tool_name, input, output) VALUES('delete', old.id, old.tool_name, old.input, old.output);
                            INSERT INTO tool_uses_fts(rowid, tool_name, input, output) VALUES (new.id, new.tool_name, new.input, new.output);
                        END");

                    transaction.Commit();
                    _ftsAvailable = true;
                    System.Diagnostics.Debug.WriteLine("[SessionDatabase] FTS5 initialized successfully");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionDatabase] FTS5 not available, using LIKE fallback: {ex.Message}");
                _ftsAvailable = false;
            }
        }

        private void UpgradeSchemaIfNeeded()
        {
            int currentVersion = 0;
            try
            {
                using (var cmd = new SQLiteCommand("SELECT version FROM schema_version LIMIT 1", _connection))
                {
                    var result = cmd.ExecuteScalar();
                    if (result != null)
                        currentVersion = Convert.ToInt32(result);
                }
            }
            catch
            {
                // schema_version table doesn't exist, treat as version 0
            }

            // Apply migrations as needed
            if (currentVersion < 1)
            {
                InitializeSchema();
            }
            else
            {
                // Check if FTS tables exist and set availability flag
                CheckFtsAvailability();
            }

            // Migration to version 2: Add chat_messages table
            if (currentVersion >= 1 && currentVersion < 2)
            {
                try
                {
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS chat_messages (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            message_id TEXT UNIQUE NOT NULL,
                            from_terminal TEXT NOT NULL,
                            to_terminal TEXT NOT NULL,
                            content TEXT NOT NULL,
                            timestamp TEXT NOT NULL,
                            is_broadcast INTEGER DEFAULT 0,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP
                        )");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_chat_messages_timestamp ON chat_messages(timestamp)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_chat_messages_terminals ON chat_messages(from_terminal, to_terminal)");
                    ExecuteNonQuery("INSERT OR REPLACE INTO schema_version (version) VALUES (2)");
                }
                catch
                {
                    // Ignore errors if table already exists
                }
            }

            // Migration to version 3: Add terminal_identities table
            if (currentVersion >= 2 && currentVersion < 3)
            {
                try
                {
                    ExecuteNonQuery(@"
                        CREATE TABLE IF NOT EXISTS terminal_identities (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            name TEXT NOT NULL,
                            project_hash TEXT NOT NULL,
                            project_path TEXT,
                            session_id TEXT NOT NULL,
                            first_prompt TEXT,
                            last_active TEXT NOT NULL,
                            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                            UNIQUE(name, project_hash)
                        )");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_terminal_identities_name ON terminal_identities(name)");
                    ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_terminal_identities_project ON terminal_identities(project_hash)");
                    ExecuteNonQuery("INSERT OR REPLACE INTO schema_version (version) VALUES (3)");
                }
                catch
                {
                    // Ignore errors if table already exists
                }
            }
        }

        private void CheckFtsAvailability()
        {
            try
            {
                using (var cmd = new SQLiteCommand(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='messages_fts'", _connection))
                {
                    var result = cmd.ExecuteScalar();
                    _ftsAvailable = result != null && Convert.ToInt32(result) > 0;
                }
            }
            catch
            {
                _ftsAvailable = false;
            }
        }

        #endregion

        #region Session CRUD

        /// <summary>
        /// Creates a new session.
        /// </summary>
        public Session CreateSession(string sessionId, string projectPath = null, string initialPrompt = null)
        {
            var session = new Session
            {
                SessionId = sessionId ?? Guid.NewGuid().ToString(),
                ProjectPath = projectPath ?? _projectPath ?? "",
                StartedAt = DateTime.Now,
                InitialPrompt = initialPrompt
            };

            using (var cmd = new SQLiteCommand(@"
                INSERT INTO sessions (session_id, project_path, started_at, initial_prompt)
                VALUES (@sessionId, @projectPath, @startedAt, @initialPrompt);
                SELECT last_insert_rowid();", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", session.SessionId);
                cmd.Parameters.AddWithValue("@projectPath", session.ProjectPath);
                cmd.Parameters.AddWithValue("@startedAt", session.StartedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@initialPrompt", (object)session.InitialPrompt ?? DBNull.Value);

                session.Id = Convert.ToInt64(cmd.ExecuteScalar());
            }

            return session;
        }

        /// <summary>
        /// Gets a session by its database ID.
        /// </summary>
        public Session GetSession(long id)
        {
            using (var cmd = new SQLiteCommand("SELECT * FROM sessions WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return MapSession(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets a session by its session ID string.
        /// </summary>
        public Session GetSessionBySessionId(string sessionId)
        {
            using (var cmd = new SQLiteCommand("SELECT * FROM sessions WHERE session_id = @sessionId", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return MapSession(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets all sessions, optionally filtered by date range.
        /// </summary>
        public List<Session> GetSessions(DateTime? startDate = null, DateTime? endDate = null, int limit = 100, int offset = 0)
        {
            var sessions = new List<Session>();
            var sql = "SELECT * FROM sessions WHERE 1=1";

            if (startDate.HasValue)
                sql += " AND started_at >= @startDate";
            if (endDate.HasValue)
                sql += " AND started_at <= @endDate";

            sql += " ORDER BY started_at DESC LIMIT @limit OFFSET @offset";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                if (startDate.HasValue)
                    cmd.Parameters.AddWithValue("@startDate", startDate.Value.ToString("o"));
                if (endDate.HasValue)
                    cmd.Parameters.AddWithValue("@endDate", endDate.Value.ToString("o"));
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.Parameters.AddWithValue("@offset", offset);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        sessions.Add(MapSession(reader));
                }
            }
            return sessions;
        }

        /// <summary>
        /// Gets recent sessions.
        /// </summary>
        public List<Session> GetRecentSessions(int count = 10)
        {
            return GetSessions(limit: count);
        }

        /// <summary>
        /// Gets sessions for a specific project path, ordered by most recent first.
        /// </summary>
        public List<Session> GetSessionsByProject(string projectPath, int limit = 20)
        {
            var sessions = new List<Session>();
            var sql = @"SELECT * FROM sessions
                        WHERE project_path = @projectPath
                        ORDER BY started_at DESC
                        LIMIT @limit";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@projectPath", projectPath);
                cmd.Parameters.AddWithValue("@limit", limit);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        sessions.Add(MapSession(reader));
                }
            }
            return sessions;
        }

        /// <summary>
        /// Updates a session.
        /// </summary>
        public void UpdateSession(Session session)
        {
            using (var cmd = new SQLiteCommand(@"
                UPDATE sessions SET
                    ended_at = @endedAt,
                    summary = @summary,
                    total_messages = @totalMessages,
                    total_tool_calls = @totalToolCalls,
                    total_cost = @totalCost,
                    input_tokens = @inputTokens,
                    output_tokens = @outputTokens
                WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", session.Id);
                cmd.Parameters.AddWithValue("@endedAt", session.EndedAt.HasValue ? (object)session.EndedAt.Value.ToString("o") : DBNull.Value);
                cmd.Parameters.AddWithValue("@summary", (object)session.Summary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@totalMessages", session.TotalMessages);
                cmd.Parameters.AddWithValue("@totalToolCalls", session.TotalToolCalls);
                cmd.Parameters.AddWithValue("@totalCost", session.TotalCost);
                cmd.Parameters.AddWithValue("@inputTokens", session.InputTokens);
                cmd.Parameters.AddWithValue("@outputTokens", session.OutputTokens);

                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Ends a session by setting the ended_at timestamp.
        /// </summary>
        public void EndSession(long sessionId)
        {
            using (var cmd = new SQLiteCommand("UPDATE sessions SET ended_at = @endedAt WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.Parameters.AddWithValue("@endedAt", DateTime.Now.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Deletes a session and all related records.
        /// </summary>
        public void DeleteSession(long sessionId)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM sessions WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Gets the total session count.
        /// </summary>
        public int GetSessionCount()
        {
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM sessions", _connection))
            {
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        #endregion

        #region ISessionDatabase Implementation

        /// <summary>
        /// Checks if a session exists by its session ID string.
        /// </summary>
        /// <param name="sessionId">The session ID string.</param>
        /// <returns>True if the session exists, false otherwise.</returns>
        public bool SessionExists(string sessionId)
        {
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM sessions WHERE session_id = @sessionId", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>
        /// Gets the last modified date for a session by its session ID string.
        /// </summary>
        /// <param name="sessionId">The session ID string.</param>
        /// <returns>The last modified date, or null if session not found.</returns>
        public DateTime? GetSessionLastModified(string sessionId)
        {
            using (var cmd = new SQLiteCommand(@"
                SELECT COALESCE(ended_at, started_at) FROM sessions WHERE session_id = @sessionId", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    return DateTime.Parse(result.ToString());
                }
            }
            return null;
        }

        /// <summary>
        /// Inserts or updates a session from a ClaudeSession object.
        /// </summary>
        /// <param name="session">The ClaudeSession to upsert.</param>
        public void UpsertSession(ClaudeSession session)
        {
            using (var cmd = new SQLiteCommand(@"
                INSERT INTO sessions (session_id, project_path, started_at, ended_at, initial_prompt, summary, total_messages)
                VALUES (@sessionId, @projectPath, @startedAt, @endedAt, @initialPrompt, @summary, @totalMessages)
                ON CONFLICT(session_id) DO UPDATE SET
                    project_path = @projectPath,
                    ended_at = @endedAt,
                    initial_prompt = @initialPrompt,
                    summary = @summary,
                    total_messages = @totalMessages", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", session.SessionId);
                cmd.Parameters.AddWithValue("@projectPath", (object)session.ProjectPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@startedAt", session.Created.ToString("o"));
                cmd.Parameters.AddWithValue("@endedAt", session.Modified != default ? (object)session.Modified.ToString("o") : DBNull.Value);
                cmd.Parameters.AddWithValue("@initialPrompt", (object)session.FirstPrompt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@summary", (object)session.Summary ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@totalMessages", session.MessageCount);

                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Inserts or updates a message from a ClaudeSessionMessage object.
        /// </summary>
        /// <param name="message">The ClaudeSessionMessage to upsert.</param>
        public void UpsertMessage(ClaudeSessionMessage message)
        {
            // First, get the internal session ID from the session_id string
            long? internalSessionId = null;
            using (var cmd = new SQLiteCommand("SELECT id FROM sessions WHERE session_id = @sessionId", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", message.SessionId);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                {
                    internalSessionId = Convert.ToInt64(result);
                }
            }

            if (!internalSessionId.HasValue)
            {
                // Session doesn't exist, cannot add message
                System.Diagnostics.Debug.WriteLine($"[SessionDatabase] Cannot add message - session not found: {message.SessionId}");
                return;
            }

            // Use UUID as unique identifier for upsert if available
            if (!string.IsNullOrEmpty(message.Uuid))
            {
                // Check if message with this UUID already exists
                using (var cmd = new SQLiteCommand(@"
                    SELECT id FROM messages
                    WHERE session_id = @sessionId AND content LIKE @uuidPattern", _connection))
                {
                    cmd.Parameters.AddWithValue("@sessionId", internalSessionId.Value);
                    cmd.Parameters.AddWithValue("@uuidPattern", $"%{message.Uuid}%");
                    var existing = cmd.ExecuteScalar();

                    if (existing != null && existing != DBNull.Value)
                    {
                        // Message exists, update it
                        using (var updateCmd = new SQLiteCommand(@"
                            UPDATE messages SET
                                role = @role,
                                content = @content,
                                timestamp = @timestamp,
                                message_type = @messageType
                            WHERE id = @id", _connection))
                        {
                            updateCmd.Parameters.AddWithValue("@id", Convert.ToInt64(existing));
                            updateCmd.Parameters.AddWithValue("@role", (object)message.Role ?? DBNull.Value);
                            updateCmd.Parameters.AddWithValue("@content", (object)message.Content ?? DBNull.Value);
                            updateCmd.Parameters.AddWithValue("@timestamp", message.Timestamp.ToString("o"));
                            updateCmd.Parameters.AddWithValue("@messageType", (object)message.Type ?? DBNull.Value);
                            updateCmd.ExecuteNonQuery();
                        }
                        return;
                    }
                }
            }

            // Insert new message
            using (var cmd = new SQLiteCommand(@"
                INSERT INTO messages (session_id, role, content, timestamp, message_type)
                VALUES (@sessionId, @role, @content, @timestamp, @messageType)", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", internalSessionId.Value);
                cmd.Parameters.AddWithValue("@role", (object)message.Role ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@content", (object)message.Content ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@timestamp", message.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("@messageType", (object)message.Type ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            // Also store tool uses if present
            if (message.ToolUses != null && message.ToolUses.Count > 0)
            {
                foreach (var toolUse in message.ToolUses)
                {
                    using (var cmd = new SQLiteCommand(@"
                        INSERT INTO tool_uses (session_id, tool_name, input, timestamp)
                        VALUES (@sessionId, @toolName, @input, @timestamp)", _connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", internalSessionId.Value);
                        cmd.Parameters.AddWithValue("@toolName", toolUse.Name ?? "unknown");
                        cmd.Parameters.AddWithValue("@input", (object)toolUse.Input ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@timestamp", message.Timestamp.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            // Store file references if present
            if (message.FileReferences != null && message.FileReferences.Count > 0)
            {
                foreach (var filePath in message.FileReferences)
                {
                    using (var cmd = new SQLiteCommand(@"
                        INSERT INTO file_refs (session_id, file_path, operation, timestamp)
                        VALUES (@sessionId, @filePath, @operation, @timestamp)", _connection))
                    {
                        cmd.Parameters.AddWithValue("@sessionId", internalSessionId.Value);
                        cmd.Parameters.AddWithValue("@filePath", filePath);
                        cmd.Parameters.AddWithValue("@operation", "reference");
                        cmd.Parameters.AddWithValue("@timestamp", message.Timestamp.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            // Store error if present
            if (message.IsError && !string.IsNullOrEmpty(message.ErrorMessage))
            {
                using (var cmd = new SQLiteCommand(@"
                    INSERT INTO errors (session_id, error_type, error_message, timestamp)
                    VALUES (@sessionId, @errorType, @errorMessage, @timestamp)", _connection))
                {
                    cmd.Parameters.AddWithValue("@sessionId", internalSessionId.Value);
                    cmd.Parameters.AddWithValue("@errorType", "tool_error");
                    cmd.Parameters.AddWithValue("@errorMessage", message.ErrorMessage);
                    cmd.Parameters.AddWithValue("@timestamp", message.Timestamp.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Deletes a session by its session ID string.
        /// </summary>
        /// <param name="sessionId">The session ID string to delete.</param>
        void ISessionDatabase.DeleteSession(string sessionId)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM sessions WHERE session_id = @sessionId", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Gets all session IDs for a given project path.
        /// </summary>
        /// <param name="projectPath">The project path to filter by.</param>
        /// <returns>List of session ID strings.</returns>
        public List<string> GetSessionIds(string projectPath)
        {
            var sessionIds = new List<string>();
            using (var cmd = new SQLiteCommand("SELECT session_id FROM sessions WHERE project_path = @projectPath", _connection))
            {
                cmd.Parameters.AddWithValue("@projectPath", projectPath);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        sessionIds.Add(reader.GetString(0));
                    }
                }
            }
            return sessionIds;
        }

        #endregion

        #region Message CRUD

        /// <summary>
        /// Adds a message to a session.
        /// </summary>
        public SessionMessage AddMessage(long sessionId, string role, string content, string messageType = null, int? tokenCount = null)
        {
            var message = new SessionMessage
            {
                SessionId = sessionId,
                Role = role,
                Content = content,
                Timestamp = DateTime.Now,
                MessageType = messageType,
                TokenCount = tokenCount
            };

            using (var cmd = new SQLiteCommand(@"
                INSERT INTO messages (session_id, role, content, timestamp, token_count, message_type)
                VALUES (@sessionId, @role, @content, @timestamp, @tokenCount, @messageType);
                SELECT last_insert_rowid();", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", message.SessionId);
                cmd.Parameters.AddWithValue("@role", message.Role);
                cmd.Parameters.AddWithValue("@content", (object)message.Content ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@timestamp", message.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("@tokenCount", message.TokenCount.HasValue ? (object)message.TokenCount.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@messageType", (object)message.MessageType ?? DBNull.Value);

                message.Id = Convert.ToInt64(cmd.ExecuteScalar());
            }

            // Update session message count
            ExecuteNonQuery($"UPDATE sessions SET total_messages = total_messages + 1 WHERE id = {sessionId}");

            return message;
        }

        /// <summary>
        /// Gets a message by ID.
        /// </summary>
        public SessionMessage GetMessage(long id)
        {
            using (var cmd = new SQLiteCommand("SELECT * FROM messages WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return MapMessage(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets all messages for a session.
        /// </summary>
        public List<SessionMessage> GetMessages(long sessionId)
        {
            var messages = new List<SessionMessage>();
            using (var cmd = new SQLiteCommand("SELECT * FROM messages WHERE session_id = @sessionId ORDER BY timestamp", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        messages.Add(MapMessage(reader));
                }
            }
            return messages;
        }

        /// <summary>
        /// Updates a message.
        /// </summary>
        public void UpdateMessage(SessionMessage message)
        {
            using (var cmd = new SQLiteCommand(@"
                UPDATE messages SET
                    content = @content,
                    token_count = @tokenCount,
                    message_type = @messageType
                WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", message.Id);
                cmd.Parameters.AddWithValue("@content", (object)message.Content ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tokenCount", message.TokenCount.HasValue ? (object)message.TokenCount.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@messageType", (object)message.MessageType ?? DBNull.Value);

                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Deletes a message.
        /// </summary>
        public void DeleteMessage(long id)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM messages WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        #endregion

        #region ToolUse CRUD

        /// <summary>
        /// Adds a tool use record.
        /// </summary>
        public ToolUse AddToolUse(long sessionId, string toolName, string input, string output = null,
            long? messageId = null, int? durationMs = null, bool success = true, string errorMessage = null)
        {
            var toolUse = new ToolUse
            {
                SessionId = sessionId,
                MessageId = messageId,
                ToolName = toolName,
                Input = input,
                Output = output,
                Timestamp = DateTime.Now,
                DurationMs = durationMs,
                Success = success,
                ErrorMessage = errorMessage
            };

            using (var cmd = new SQLiteCommand(@"
                INSERT INTO tool_uses (session_id, message_id, tool_name, input, output, timestamp, duration_ms, success, error_message)
                VALUES (@sessionId, @messageId, @toolName, @input, @output, @timestamp, @durationMs, @success, @errorMessage);
                SELECT last_insert_rowid();", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", toolUse.SessionId);
                cmd.Parameters.AddWithValue("@messageId", toolUse.MessageId.HasValue ? (object)toolUse.MessageId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@toolName", toolUse.ToolName);
                cmd.Parameters.AddWithValue("@input", (object)toolUse.Input ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@output", (object)toolUse.Output ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@timestamp", toolUse.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("@durationMs", toolUse.DurationMs.HasValue ? (object)toolUse.DurationMs.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@success", toolUse.Success ? 1 : 0);
                cmd.Parameters.AddWithValue("@errorMessage", (object)toolUse.ErrorMessage ?? DBNull.Value);

                toolUse.Id = Convert.ToInt64(cmd.ExecuteScalar());
            }

            // Update session tool call count
            ExecuteNonQuery($"UPDATE sessions SET total_tool_calls = total_tool_calls + 1 WHERE id = {sessionId}");

            return toolUse;
        }

        /// <summary>
        /// Gets a tool use by ID.
        /// </summary>
        public ToolUse GetToolUse(long id)
        {
            using (var cmd = new SQLiteCommand("SELECT * FROM tool_uses WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return MapToolUse(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets all tool uses for a session.
        /// </summary>
        public List<ToolUse> GetToolUses(long sessionId)
        {
            var toolUses = new List<ToolUse>();
            using (var cmd = new SQLiteCommand("SELECT * FROM tool_uses WHERE session_id = @sessionId ORDER BY timestamp", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        toolUses.Add(MapToolUse(reader));
                }
            }
            return toolUses;
        }

        /// <summary>
        /// Gets tool uses by tool name.
        /// </summary>
        public List<ToolUse> GetToolUsesByName(string toolName, int limit = 100)
        {
            var toolUses = new List<ToolUse>();
            using (var cmd = new SQLiteCommand("SELECT * FROM tool_uses WHERE tool_name = @toolName ORDER BY timestamp DESC LIMIT @limit", _connection))
            {
                cmd.Parameters.AddWithValue("@toolName", toolName);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        toolUses.Add(MapToolUse(reader));
                }
            }
            return toolUses;
        }

        /// <summary>
        /// Updates a tool use output (useful for streaming results).
        /// </summary>
        public void UpdateToolUseOutput(long id, string output, int? durationMs = null, bool? success = null, string errorMessage = null)
        {
            var sql = "UPDATE tool_uses SET output = @output";
            if (durationMs.HasValue) sql += ", duration_ms = @durationMs";
            if (success.HasValue) sql += ", success = @success";
            if (errorMessage != null) sql += ", error_message = @errorMessage";
            sql += " WHERE id = @id";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@output", (object)output ?? DBNull.Value);
                if (durationMs.HasValue) cmd.Parameters.AddWithValue("@durationMs", durationMs.Value);
                if (success.HasValue) cmd.Parameters.AddWithValue("@success", success.Value ? 1 : 0);
                if (errorMessage != null) cmd.Parameters.AddWithValue("@errorMessage", errorMessage);

                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Deletes a tool use.
        /// </summary>
        public void DeleteToolUse(long id)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM tool_uses WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Gets distinct tool names used across all sessions.
        /// </summary>
        public List<string> GetDistinctToolNames()
        {
            var names = new List<string>();
            using (var cmd = new SQLiteCommand("SELECT DISTINCT tool_name FROM tool_uses ORDER BY tool_name", _connection))
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        names.Add(reader.GetString(0));
                }
            }
            return names;
        }

        #endregion

        #region FileRef CRUD

        /// <summary>
        /// Adds a file reference.
        /// </summary>
        public FileRef AddFileRef(long sessionId, string filePath, string operation, long? toolUseId = null, int? linesChanged = null)
        {
            var fileRef = new FileRef
            {
                SessionId = sessionId,
                ToolUseId = toolUseId,
                FilePath = filePath,
                Operation = operation,
                Timestamp = DateTime.Now,
                LinesChanged = linesChanged
            };

            using (var cmd = new SQLiteCommand(@"
                INSERT INTO file_refs (session_id, tool_use_id, file_path, operation, timestamp, lines_changed)
                VALUES (@sessionId, @toolUseId, @filePath, @operation, @timestamp, @linesChanged);
                SELECT last_insert_rowid();", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", fileRef.SessionId);
                cmd.Parameters.AddWithValue("@toolUseId", fileRef.ToolUseId.HasValue ? (object)fileRef.ToolUseId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@filePath", fileRef.FilePath);
                cmd.Parameters.AddWithValue("@operation", fileRef.Operation);
                cmd.Parameters.AddWithValue("@timestamp", fileRef.Timestamp.ToString("o"));
                cmd.Parameters.AddWithValue("@linesChanged", fileRef.LinesChanged.HasValue ? (object)fileRef.LinesChanged.Value : DBNull.Value);

                fileRef.Id = Convert.ToInt64(cmd.ExecuteScalar());
            }

            return fileRef;
        }

        /// <summary>
        /// Gets a file reference by ID.
        /// </summary>
        public FileRef GetFileRef(long id)
        {
            using (var cmd = new SQLiteCommand("SELECT * FROM file_refs WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return MapFileRef(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets all file references for a session.
        /// </summary>
        public List<FileRef> GetFileRefs(long sessionId)
        {
            var fileRefs = new List<FileRef>();
            using (var cmd = new SQLiteCommand("SELECT * FROM file_refs WHERE session_id = @sessionId ORDER BY timestamp", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        fileRefs.Add(MapFileRef(reader));
                }
            }
            return fileRefs;
        }

        /// <summary>
        /// Gets file references by file path (across all sessions).
        /// </summary>
        public List<FileRef> GetFileRefsByPath(string filePath, int limit = 100)
        {
            var fileRefs = new List<FileRef>();
            using (var cmd = new SQLiteCommand("SELECT * FROM file_refs WHERE file_path = @filePath ORDER BY timestamp DESC LIMIT @limit", _connection))
            {
                cmd.Parameters.AddWithValue("@filePath", filePath);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        fileRefs.Add(MapFileRef(reader));
                }
            }
            return fileRefs;
        }

        /// <summary>
        /// Gets file references by operation type.
        /// </summary>
        public List<FileRef> GetFileRefsByOperation(string operation, long? sessionId = null, int limit = 100)
        {
            var fileRefs = new List<FileRef>();
            var sql = "SELECT * FROM file_refs WHERE operation = @operation";
            if (sessionId.HasValue) sql += " AND session_id = @sessionId";
            sql += " ORDER BY timestamp DESC LIMIT @limit";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@operation", operation);
                if (sessionId.HasValue) cmd.Parameters.AddWithValue("@sessionId", sessionId.Value);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        fileRefs.Add(MapFileRef(reader));
                }
            }
            return fileRefs;
        }

        /// <summary>
        /// Deletes a file reference.
        /// </summary>
        public void DeleteFileRef(long id)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM file_refs WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        #endregion

        #region Error CRUD

        /// <summary>
        /// Adds an error record.
        /// </summary>
        public SessionError AddError(long sessionId, string errorType, string errorMessage,
            string stackTrace = null, long? messageId = null, long? toolUseId = null)
        {
            var error = new SessionError
            {
                SessionId = sessionId,
                MessageId = messageId,
                ToolUseId = toolUseId,
                ErrorType = errorType,
                ErrorMessage = errorMessage,
                StackTrace = stackTrace,
                Timestamp = DateTime.Now
            };

            using (var cmd = new SQLiteCommand(@"
                INSERT INTO errors (session_id, message_id, tool_use_id, error_type, error_message, stack_trace, timestamp)
                VALUES (@sessionId, @messageId, @toolUseId, @errorType, @errorMessage, @stackTrace, @timestamp);
                SELECT last_insert_rowid();", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", error.SessionId);
                cmd.Parameters.AddWithValue("@messageId", error.MessageId.HasValue ? (object)error.MessageId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@toolUseId", error.ToolUseId.HasValue ? (object)error.ToolUseId.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@errorType", (object)error.ErrorType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@errorMessage", (object)error.ErrorMessage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@stackTrace", (object)error.StackTrace ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@timestamp", error.Timestamp.ToString("o"));

                error.Id = Convert.ToInt64(cmd.ExecuteScalar());
            }

            return error;
        }

        /// <summary>
        /// Gets an error by ID.
        /// </summary>
        public SessionError GetError(long id)
        {
            using (var cmd = new SQLiteCommand("SELECT * FROM errors WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return MapError(reader);
                }
            }
            return null;
        }

        /// <summary>
        /// Gets all errors for a session.
        /// </summary>
        public List<SessionError> GetErrors(long sessionId)
        {
            var errors = new List<SessionError>();
            using (var cmd = new SQLiteCommand("SELECT * FROM errors WHERE session_id = @sessionId ORDER BY timestamp", _connection))
            {
                cmd.Parameters.AddWithValue("@sessionId", sessionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        errors.Add(MapError(reader));
                }
            }
            return errors;
        }

        /// <summary>
        /// Gets errors by type.
        /// </summary>
        public List<SessionError> GetErrorsByType(string errorType, int limit = 100)
        {
            var errors = new List<SessionError>();
            using (var cmd = new SQLiteCommand("SELECT * FROM errors WHERE error_type = @errorType ORDER BY timestamp DESC LIMIT @limit", _connection))
            {
                cmd.Parameters.AddWithValue("@errorType", errorType);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        errors.Add(MapError(reader));
                }
            }
            return errors;
        }

        /// <summary>
        /// Deletes an error.
        /// </summary>
        public void DeleteError(long id)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM errors WHERE id = @id", _connection))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        #endregion

        #region Full-Text Search

        /// <summary>
        /// Searches messages using FTS5 or LIKE fallback.
        /// </summary>
        public List<SessionMessage> SearchMessages(string query, int limit = 50)
        {
            var messages = new List<SessionMessage>();

            if (_ftsAvailable)
            {
                // Use FTS5 for fast full-text search
                string ftsQuery = EscapeFtsQuery(query);

                using (var cmd = new SQLiteCommand(@"
                    SELECT m.* FROM messages m
                    INNER JOIN messages_fts f ON m.id = f.rowid
                    WHERE messages_fts MATCH @query
                    ORDER BY rank
                    LIMIT @limit", _connection))
                {
                    cmd.Parameters.AddWithValue("@query", ftsQuery);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            messages.Add(MapMessage(reader));
                    }
                }
            }
            else
            {
                // Fallback to LIKE search
                string pattern = $"%{query}%";

                using (var cmd = new SQLiteCommand(@"
                    SELECT * FROM messages
                    WHERE content LIKE @pattern
                    ORDER BY timestamp DESC
                    LIMIT @limit", _connection))
                {
                    cmd.Parameters.AddWithValue("@pattern", pattern);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            messages.Add(MapMessage(reader));
                    }
                }
            }
            return messages;
        }

        /// <summary>
        /// Searches tool uses using FTS5 or LIKE fallback.
        /// </summary>
        public List<ToolUse> SearchToolUses(string query, int limit = 50)
        {
            var toolUses = new List<ToolUse>();

            if (_ftsAvailable)
            {
                string ftsQuery = EscapeFtsQuery(query);

                using (var cmd = new SQLiteCommand(@"
                    SELECT t.* FROM tool_uses t
                    INNER JOIN tool_uses_fts f ON t.id = f.rowid
                    WHERE tool_uses_fts MATCH @query
                    ORDER BY rank
                    LIMIT @limit", _connection))
                {
                    cmd.Parameters.AddWithValue("@query", ftsQuery);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            toolUses.Add(MapToolUse(reader));
                    }
                }
            }
            else
            {
                // Fallback to LIKE search
                string pattern = $"%{query}%";

                using (var cmd = new SQLiteCommand(@"
                    SELECT * FROM tool_uses
                    WHERE tool_name LIKE @pattern OR input LIKE @pattern OR output LIKE @pattern
                    ORDER BY timestamp DESC
                    LIMIT @limit", _connection))
                {
                    cmd.Parameters.AddWithValue("@pattern", pattern);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            toolUses.Add(MapToolUse(reader));
                    }
                }
            }
            return toolUses;
        }

        /// <summary>
        /// Performs a combined search across messages and tool uses.
        /// </summary>
        public List<SearchResult> SearchAll(string query, int limit = 50)
        {
            var results = new List<SearchResult>();

            if (_ftsAvailable)
            {
                string ftsQuery = EscapeFtsQuery(query);

                using (var cmd = new SQLiteCommand(@"
                    SELECT 'messages' as table_name, m.id, m.content as matched_text, rank
                    FROM messages m
                    INNER JOIN messages_fts f ON m.id = f.rowid
                    WHERE messages_fts MATCH @query
                    UNION ALL
                    SELECT 'tool_uses' as table_name, t.id, t.tool_name || ': ' || COALESCE(t.input, '') as matched_text, rank
                    FROM tool_uses t
                    INNER JOIN tool_uses_fts f ON t.id = f.rowid
                    WHERE tool_uses_fts MATCH @query
                    ORDER BY rank
                    LIMIT @limit", _connection))
                {
                    cmd.Parameters.AddWithValue("@query", ftsQuery);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new SearchResult
                            {
                                TableName = reader.GetString(0),
                                RecordId = reader.GetInt64(1),
                                MatchedText = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Rank = reader.GetDouble(3)
                            });
                        }
                    }
                }
            }
            else
            {
                // Fallback to LIKE search
                string pattern = $"%{query}%";

                using (var cmd = new SQLiteCommand(@"
                    SELECT 'messages' as table_name, id, content as matched_text, 0.0 as rank
                    FROM messages
                    WHERE content LIKE @pattern
                    UNION ALL
                    SELECT 'tool_uses' as table_name, id, tool_name || ': ' || COALESCE(input, '') as matched_text, 0.0 as rank
                    FROM tool_uses
                    WHERE tool_name LIKE @pattern OR input LIKE @pattern OR output LIKE @pattern
                    ORDER BY rank
                    LIMIT @limit", _connection))
                {
                    cmd.Parameters.AddWithValue("@pattern", pattern);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new SearchResult
                            {
                                TableName = reader.GetString(0),
                                RecordId = reader.GetInt64(1),
                                MatchedText = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Rank = reader.GetDouble(3)
                            });
                        }
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Searches sessions by initial prompt or summary.
        /// </summary>
        public List<Session> SearchSessions(string query, int limit = 50)
        {
            var sessions = new List<Session>();
            string pattern = $"%{query}%";

            using (var cmd = new SQLiteCommand(@"
                SELECT * FROM sessions
                WHERE initial_prompt LIKE @pattern OR summary LIKE @pattern
                ORDER BY started_at DESC
                LIMIT @limit", _connection))
            {
                cmd.Parameters.AddWithValue("@pattern", pattern);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        sessions.Add(MapSession(reader));
                }
            }
            return sessions;
        }

        /// <summary>
        /// Searches message content and returns results with session context.
        /// </summary>
        public List<MessageSearchResult> SearchMessagesWithSessionInfo(string query, string projectPath = null, int limit = 50)
        {
            var results = new List<MessageSearchResult>();
            string pattern = $"%{query}%";

            // Build WHERE clause for optional project filter
            string projectFilter = projectPath != null ? "AND s.project_path = @projectPath" : "";

            using (var cmd = new SQLiteCommand($@"
                SELECT m.id as message_id, s.session_id, s.summary, s.initial_prompt, m.role, m.content, m.timestamp
                FROM messages m
                INNER JOIN sessions s ON m.session_id = s.id
                WHERE m.content LIKE @pattern {projectFilter}
                ORDER BY m.timestamp DESC
                LIMIT @limit", _connection))
            {
                cmd.Parameters.AddWithValue("@pattern", pattern);
                cmd.Parameters.AddWithValue("@limit", limit);
                if (projectPath != null)
                    cmd.Parameters.AddWithValue("@projectPath", projectPath);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string content = reader.IsDBNull(5) ? "" : reader.GetString(5);
                        // Create a snippet around the match (first 200 chars or centered on match)
                        string snippet = CreateSnippet(content, query, 200);

                        results.Add(new MessageSearchResult
                        {
                            MessageId = reader.GetInt64(0),
                            SessionId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            SessionSummary = reader.IsDBNull(2) ? (reader.IsDBNull(3) ? "" : reader.GetString(3)) : reader.GetString(2),
                            Role = reader.IsDBNull(4) ? "" : reader.GetString(4),
                            ContentSnippet = snippet,
                            Timestamp = reader.IsDBNull(6) ? DateTime.MinValue : DateTime.Parse(reader.GetString(6))
                        });
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Creates a text snippet centered around the search query match.
        /// </summary>
        private string CreateSnippet(string content, string query, int maxLength)
        {
            if (string.IsNullOrEmpty(content))
                return "";

            int matchIndex = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
                return content.Length <= maxLength ? content : content.Substring(0, maxLength) + "...";

            // Center the snippet around the match
            int start = Math.Max(0, matchIndex - maxLength / 2);
            int end = Math.Min(content.Length, start + maxLength);

            // Adjust start if we're near the end
            if (end == content.Length && end - start < maxLength)
                start = Math.Max(0, end - maxLength);

            string snippet = content.Substring(start, end - start);

            // Add ellipsis if truncated
            if (start > 0) snippet = "..." + snippet;
            if (end < content.Length) snippet = snippet + "...";

            return snippet;
        }

        /// <summary>
        /// Escapes special characters for FTS5 queries.
        /// </summary>
        private string EscapeFtsQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "\"\"";

            // Wrap in quotes for phrase search, escape internal quotes
            return "\"" + query.Replace("\"", "\"\"") + "\"";
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Gets statistics for the database.
        /// </summary>
        public Dictionary<string, object> GetStatistics()
        {
            var stats = new Dictionary<string, object>();

            stats["TotalSessions"] = ExecuteScalar<long>("SELECT COUNT(*) FROM sessions");
            stats["TotalMessages"] = ExecuteScalar<long>("SELECT COUNT(*) FROM messages");
            stats["TotalToolUses"] = ExecuteScalar<long>("SELECT COUNT(*) FROM tool_uses");
            stats["TotalFileRefs"] = ExecuteScalar<long>("SELECT COUNT(*) FROM file_refs");
            stats["TotalErrors"] = ExecuteScalar<long>("SELECT COUNT(*) FROM errors");
            stats["TotalCost"] = ExecuteScalar<decimal>("SELECT COALESCE(SUM(total_cost), 0) FROM sessions");
            stats["TotalInputTokens"] = ExecuteScalar<long>("SELECT COALESCE(SUM(input_tokens), 0) FROM sessions");
            stats["TotalOutputTokens"] = ExecuteScalar<long>("SELECT COALESCE(SUM(output_tokens), 0) FROM sessions");
            stats["DatabaseSizeBytes"] = new FileInfo(_databasePath).Length;

            return stats;
        }

        /// <summary>
        /// Gets tool usage statistics.
        /// </summary>
        public Dictionary<string, int> GetToolUsageStats()
        {
            var stats = new Dictionary<string, int>();
            using (var cmd = new SQLiteCommand("SELECT tool_name, COUNT(*) as count FROM tool_uses GROUP BY tool_name ORDER BY count DESC", _connection))
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        stats[reader.GetString(0)] = reader.GetInt32(1);
                    }
                }
            }
            return stats;
        }

        /// <summary>
        /// Gets file operation statistics.
        /// </summary>
        public Dictionary<string, int> GetFileOperationStats()
        {
            var stats = new Dictionary<string, int>();
            using (var cmd = new SQLiteCommand("SELECT operation, COUNT(*) as count FROM file_refs GROUP BY operation ORDER BY count DESC", _connection))
            {
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        stats[reader.GetString(0)] = reader.GetInt32(1);
                    }
                }
            }
            return stats;
        }

        #endregion

        #region Maintenance

        /// <summary>
        /// Optimizes the database (VACUUM).
        /// </summary>
        public void Optimize()
        {
            ExecuteNonQuery("VACUUM");
        }

        /// <summary>
        /// Rebuilds FTS indexes if available.
        /// </summary>
        public void RebuildFtsIndexes()
        {
            if (!_ftsAvailable)
            {
                System.Diagnostics.Debug.WriteLine("[SessionDatabase] Cannot rebuild FTS indexes - FTS5 not available");
                return;
            }

            ExecuteNonQuery("INSERT INTO messages_fts(messages_fts) VALUES('rebuild')");
            ExecuteNonQuery("INSERT INTO tool_uses_fts(tool_uses_fts) VALUES('rebuild')");
        }

        /// <summary>
        /// Deletes sessions older than the specified date.
        /// </summary>
        public int PurgeSessions(DateTime olderThan)
        {
            using (var cmd = new SQLiteCommand("DELETE FROM sessions WHERE started_at < @date", _connection))
            {
                cmd.Parameters.AddWithValue("@date", olderThan.ToString("o"));
                return cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Gets the database file size in bytes.
        /// </summary>
        public long GetDatabaseSize()
        {
            return new FileInfo(_databasePath).Length;
        }

        #endregion

        #region Private Helpers

        private void ExecuteNonQuery(string sql)
        {
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.ExecuteNonQuery();
            }
        }

        private T ExecuteScalar<T>(string sql)
        {
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return default(T);
                return (T)Convert.ChangeType(result, typeof(T));
            }
        }

        private Session MapSession(SQLiteDataReader reader)
        {
            return new Session
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                ProjectPath = reader.GetString(reader.GetOrdinal("project_path")),
                StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
                EndedAt = reader.IsDBNull(reader.GetOrdinal("ended_at")) ? (DateTime?)null : DateTime.Parse(reader.GetString(reader.GetOrdinal("ended_at"))),
                InitialPrompt = reader.IsDBNull(reader.GetOrdinal("initial_prompt")) ? null : reader.GetString(reader.GetOrdinal("initial_prompt")),
                Summary = reader.IsDBNull(reader.GetOrdinal("summary")) ? null : reader.GetString(reader.GetOrdinal("summary")),
                TotalMessages = reader.GetInt32(reader.GetOrdinal("total_messages")),
                TotalToolCalls = reader.GetInt32(reader.GetOrdinal("total_tool_calls")),
                TotalCost = reader.GetDecimal(reader.GetOrdinal("total_cost")),
                InputTokens = reader.GetInt32(reader.GetOrdinal("input_tokens")),
                OutputTokens = reader.GetInt32(reader.GetOrdinal("output_tokens"))
            };
        }

        private SessionMessage MapMessage(SQLiteDataReader reader)
        {
            return new SessionMessage
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SessionId = reader.GetInt64(reader.GetOrdinal("session_id")),
                Role = reader.GetString(reader.GetOrdinal("role")),
                Content = reader.IsDBNull(reader.GetOrdinal("content")) ? null : reader.GetString(reader.GetOrdinal("content")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                TokenCount = reader.IsDBNull(reader.GetOrdinal("token_count")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("token_count")),
                MessageType = reader.IsDBNull(reader.GetOrdinal("message_type")) ? null : reader.GetString(reader.GetOrdinal("message_type"))
            };
        }

        private ToolUse MapToolUse(SQLiteDataReader reader)
        {
            return new ToolUse
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SessionId = reader.GetInt64(reader.GetOrdinal("session_id")),
                MessageId = reader.IsDBNull(reader.GetOrdinal("message_id")) ? (long?)null : reader.GetInt64(reader.GetOrdinal("message_id")),
                ToolName = reader.GetString(reader.GetOrdinal("tool_name")),
                Input = reader.IsDBNull(reader.GetOrdinal("input")) ? null : reader.GetString(reader.GetOrdinal("input")),
                Output = reader.IsDBNull(reader.GetOrdinal("output")) ? null : reader.GetString(reader.GetOrdinal("output")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                DurationMs = reader.IsDBNull(reader.GetOrdinal("duration_ms")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("duration_ms")),
                Success = reader.GetInt32(reader.GetOrdinal("success")) != 0,
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message"))
            };
        }

        private FileRef MapFileRef(SQLiteDataReader reader)
        {
            return new FileRef
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SessionId = reader.GetInt64(reader.GetOrdinal("session_id")),
                ToolUseId = reader.IsDBNull(reader.GetOrdinal("tool_use_id")) ? (long?)null : reader.GetInt64(reader.GetOrdinal("tool_use_id")),
                FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                Operation = reader.GetString(reader.GetOrdinal("operation")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                LinesChanged = reader.IsDBNull(reader.GetOrdinal("lines_changed")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("lines_changed"))
            };
        }

        private SessionError MapError(SQLiteDataReader reader)
        {
            return new SessionError
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                SessionId = reader.GetInt64(reader.GetOrdinal("session_id")),
                MessageId = reader.IsDBNull(reader.GetOrdinal("message_id")) ? (long?)null : reader.GetInt64(reader.GetOrdinal("message_id")),
                ToolUseId = reader.IsDBNull(reader.GetOrdinal("tool_use_id")) ? (long?)null : reader.GetInt64(reader.GetOrdinal("tool_use_id")),
                ErrorType = reader.IsDBNull(reader.GetOrdinal("error_type")) ? null : reader.GetString(reader.GetOrdinal("error_type")),
                ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message")),
                StackTrace = reader.IsDBNull(reader.GetOrdinal("stack_trace")) ? null : reader.GetString(reader.GetOrdinal("stack_trace")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp")))
            };
        }

        #endregion

        #region Chat Messages (MCP Inter-Terminal)

        /// <summary>
        /// Save a chat message from inter-terminal communication.
        /// </summary>
        public void SaveChatMessage(string messageId, string fromTerminal, string toTerminal, string content, DateTime timestamp, bool isBroadcast)
        {

            const string sql = @"
                INSERT OR IGNORE INTO chat_messages (message_id, from_terminal, to_terminal, content, timestamp, is_broadcast)
                VALUES (@messageId, @fromTerminal, @toTerminal, @content, @timestamp, @isBroadcast)";

            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@fromTerminal", fromTerminal);
                command.Parameters.AddWithValue("@toTerminal", toTerminal);
                command.Parameters.AddWithValue("@content", content);
                command.Parameters.AddWithValue("@timestamp", timestamp.ToString("o"));
                command.Parameters.AddWithValue("@isBroadcast", isBroadcast ? 1 : 0);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Save a LEARNED message for semantic memory indexing.
        /// </summary>
        public void SaveLearnedMessage(string messageId, string instance, string topic, string summary, IEnumerable<string> tags, DateTime timestamp)
        {
            const string sql = @"
                INSERT OR IGNORE INTO learned_messages (message_id, instance, topic, summary, tags, timestamp)
                VALUES (@messageId, @instance, @topic, @summary, @tags, @timestamp)";

            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@messageId", messageId);
                command.Parameters.AddWithValue("@instance", instance);
                command.Parameters.AddWithValue("@topic", topic);
                command.Parameters.AddWithValue("@summary", summary ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@tags", tags != null ? string.Join(",", tags) : (object)DBNull.Value);
                command.Parameters.AddWithValue("@timestamp", timestamp.ToString("o"));
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Get LEARNED messages, optionally filtered by instance and time range.
        /// </summary>
        public List<LearnedMessage> GetLearnedMessages(string instance = null, DateTime? since = null, int limit = 100)
        {
            var sql = "SELECT * FROM learned_messages WHERE 1=1";
            if (!string.IsNullOrEmpty(instance))
                sql += " AND instance = @instance";
            if (since.HasValue)
                sql += " AND timestamp >= @since";
            sql += " ORDER BY timestamp DESC LIMIT @limit";

            var messages = new List<LearnedMessage>();
            using (var command = new SQLiteCommand(sql, _connection))
            {
                if (!string.IsNullOrEmpty(instance))
                    command.Parameters.AddWithValue("@instance", instance);
                if (since.HasValue)
                    command.Parameters.AddWithValue("@since", since.Value.ToString("o"));
                command.Parameters.AddWithValue("@limit", limit);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var tagsStr = reader["tags"]?.ToString();
                        messages.Add(new LearnedMessage
                        {
                            Id = Convert.ToInt64(reader["id"]),
                            MessageId = reader["message_id"].ToString(),
                            Instance = reader["instance"].ToString(),
                            Topic = reader["topic"].ToString(),
                            Summary = reader["summary"]?.ToString(),
                            Tags = string.IsNullOrEmpty(tagsStr) ? new List<string>() : tagsStr.Split(',').ToList(),
                            Timestamp = DateTime.Parse(reader["timestamp"].ToString())
                        });
                    }
                }
            }
            return messages;
        }

        /// <summary>
        /// Get chat messages, optionally filtered by terminal and time range.
        /// </summary>
        public List<ChatMessage> GetChatMessages(string terminal = null, DateTime? since = null, int limit = 100)
        {
            
            var sql = "SELECT * FROM chat_messages WHERE 1=1";
            if (!string.IsNullOrEmpty(terminal))
                sql += " AND (from_terminal = @terminal OR to_terminal = @terminal)";
            if (since.HasValue)
                sql += " AND timestamp >= @since";
            sql += " ORDER BY timestamp DESC LIMIT @limit";

            var messages = new List<ChatMessage>();
            using (var command = new SQLiteCommand(sql, _connection))
            {
                if (!string.IsNullOrEmpty(terminal))
                    command.Parameters.AddWithValue("@terminal", terminal);
                if (since.HasValue)
                    command.Parameters.AddWithValue("@since", since.Value.ToString("o"));
                command.Parameters.AddWithValue("@limit", limit);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        messages.Add(new ChatMessage
                        {
                            Id = reader.GetInt64(reader.GetOrdinal("id")),
                            MessageId = reader.GetString(reader.GetOrdinal("message_id")),
                            FromTerminal = reader.GetString(reader.GetOrdinal("from_terminal")),
                            ToTerminal = reader.GetString(reader.GetOrdinal("to_terminal")),
                            Content = reader.GetString(reader.GetOrdinal("content")),
                            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                            IsBroadcast = reader.GetInt32(reader.GetOrdinal("is_broadcast")) == 1
                        });
                    }
                }
            }

            messages.Reverse(); // Return in chronological order
            return messages;
        }

        /// <summary>
        /// Search chat messages by content.
        /// </summary>
        public List<ChatMessage> SearchChatMessages(string query, int limit = 50)
        {
            
            const string sql = @"
                SELECT * FROM chat_messages
                WHERE content LIKE @query
                ORDER BY timestamp DESC
                LIMIT @limit";

            var messages = new List<ChatMessage>();
            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@query", $"%{query}%");
                command.Parameters.AddWithValue("@limit", limit);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        messages.Add(new ChatMessage
                        {
                            Id = reader.GetInt64(reader.GetOrdinal("id")),
                            MessageId = reader.GetString(reader.GetOrdinal("message_id")),
                            FromTerminal = reader.GetString(reader.GetOrdinal("from_terminal")),
                            ToTerminal = reader.GetString(reader.GetOrdinal("to_terminal")),
                            Content = reader.GetString(reader.GetOrdinal("content")),
                            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                            IsBroadcast = reader.GetInt32(reader.GetOrdinal("is_broadcast")) == 1
                        });
                    }
                }
            }

            messages.Reverse();
            return messages;
        }

        #endregion

        #region Terminal Identity Methods

        /// <summary>
        /// Save or update a terminal identity mapping.
        /// </summary>
        public void SaveIdentity(string name, string projectHash, string projectPath, string sessionId, string firstPrompt = null)
        {
            const string sql = @"
                INSERT INTO terminal_identities (name, project_hash, project_path, session_id, first_prompt, last_active)
                VALUES (@name, @projectHash, @projectPath, @sessionId, @firstPrompt, @lastActive)
                ON CONFLICT(name, project_hash) DO UPDATE SET
                    session_id = @sessionId,
                    project_path = @projectPath,
                    first_prompt = COALESCE(@firstPrompt, first_prompt),
                    last_active = @lastActive";

            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@projectHash", projectHash);
                command.Parameters.AddWithValue("@projectPath", (object)projectPath ?? DBNull.Value);
                command.Parameters.AddWithValue("@sessionId", sessionId);
                command.Parameters.AddWithValue("@firstPrompt", (object)firstPrompt ?? DBNull.Value);
                command.Parameters.AddWithValue("@lastActive", DateTime.UtcNow.ToString("o"));
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Get the session ID for a terminal identity in a specific project.
        /// </summary>
        public string GetSessionId(string name, string projectHash)
        {
            const string sql = "SELECT session_id FROM terminal_identities WHERE name = @name AND project_hash = @projectHash";

            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@projectHash", projectHash);
                var result = command.ExecuteScalar();
                return result as string;
            }
        }

        /// <summary>
        /// Get a terminal identity by name and project.
        /// </summary>
        public TerminalIdentity GetIdentity(string name, string projectHash)
        {
            const string sql = "SELECT * FROM terminal_identities WHERE name = @name AND project_hash = @projectHash";

            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@projectHash", projectHash);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return ReadIdentity(reader);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Get all identities for a project.
        /// </summary>
        public List<TerminalIdentity> GetIdentitiesForProject(string projectHash)
        {
            const string sql = "SELECT * FROM terminal_identities WHERE project_hash = @projectHash ORDER BY last_active DESC";

            var identities = new List<TerminalIdentity>();
            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@projectHash", projectHash);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        identities.Add(ReadIdentity(reader));
                    }
                }
            }
            return identities;
        }

        /// <summary>
        /// Get all identities across all projects.
        /// </summary>
        public List<TerminalIdentity> GetAllIdentities()
        {
            const string sql = "SELECT * FROM terminal_identities ORDER BY name, last_active DESC";

            var identities = new List<TerminalIdentity>();
            using (var command = new SQLiteCommand(sql, _connection))
            {
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        identities.Add(ReadIdentity(reader));
                    }
                }
            }
            return identities;
        }

        /// <summary>
        /// Delete a terminal identity.
        /// </summary>
        public void DeleteIdentity(string name, string projectHash)
        {
            const string sql = "DELETE FROM terminal_identities WHERE name = @name AND project_hash = @projectHash";

            using (var command = new SQLiteCommand(sql, _connection))
            {
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@projectHash", projectHash);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Helper to convert project path to the folder name Claude uses.
        /// </summary>
        public static string GetProjectHash(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;

            // Claude uses path with separators replaced by dashes
            // e.g., "H:\DevLaptop\Project" -> "H--DevLaptop-Project"
            return projectPath
                .Replace(":\\", "--")
                .Replace("\\", "-")
                .Replace("/", "-");
        }

        private TerminalIdentity ReadIdentity(SQLiteDataReader reader)
        {
            return new TerminalIdentity
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                ProjectHash = reader.GetString(reader.GetOrdinal("project_hash")),
                ProjectPath = reader.IsDBNull(reader.GetOrdinal("project_path")) ? null : reader.GetString(reader.GetOrdinal("project_path")),
                SessionId = reader.GetString(reader.GetOrdinal("session_id")),
                FirstPrompt = reader.IsDBNull(reader.GetOrdinal("first_prompt")) ? null : reader.GetString(reader.GetOrdinal("first_prompt")),
                LastActive = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_active")))
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _connection = null;
                _isDisposed = true;
            }
        }

        #endregion
    }
}
