using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace MultiTerminal.Services
{
    /// <summary>
    /// SQLite database service for persisting inter-terminal messages.
    /// Provides reliable message delivery with retry support.
    /// Database is stored at %APPDATA%\multiterminal\messages.db
    /// </summary>
    public class MessageQueueDatabase : IDisposable
    {
        private readonly string _databasePath;
        private SQLiteConnection _connection;
        private bool _isDisposed;

        /// <summary>
        /// Message delivery status values.
        /// </summary>
        public static class MessageStatus
        {
            public const string Pending = "pending";
            public const string Delivering = "delivering";
            public const string Delivered = "delivered";
            public const string Failed = "failed";
        }

        /// <summary>
        /// Gets the path to the message queue database.
        /// </summary>
        public static string GetDatabasePath()
        {
            var testDb = Environment.GetEnvironmentVariable("MULTITERMINAL_TEST_MSGDB");
            if (!string.IsNullOrEmpty(testDb)) return testDb;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "multiterminal");
            return Path.Combine(folder, "messages.db");
        }

        /// <summary>
        /// Creates a new MessageQueueDatabase instance.
        /// </summary>
        public MessageQueueDatabase()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "multiterminal");

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            _databasePath = Path.Combine(folder, "messages.db");
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
            // Create base table first
            const string tableSchema = @"
                CREATE TABLE IF NOT EXISTS message_queue (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    from_terminal TEXT NOT NULL,
                    to_terminal TEXT NOT NULL,
                    content TEXT NOT NULL,
                    status TEXT DEFAULT 'pending',
                    retry_count INTEGER DEFAULT 0,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                    delivered_at DATETIME,
                    error TEXT
                );
            ";

            using var command = new SQLiteCommand(tableSchema, _connection);
            command.ExecuteNonQuery();

            // Migrate existing tables to add new columns if they don't exist
            MigrateSchema();

            // Create indexes after migration ensures all columns exist
            const string indexSchema = @"
                CREATE INDEX IF NOT EXISTS idx_message_queue_status ON message_queue(status);
                CREATE INDEX IF NOT EXISTS idx_message_queue_to_terminal ON message_queue(to_terminal);
                CREATE INDEX IF NOT EXISTS idx_message_queue_created_at ON message_queue(created_at);
                CREATE INDEX IF NOT EXISTS idx_message_queue_notification_type ON message_queue(notification_type);
                CREATE INDEX IF NOT EXISTS idx_message_queue_reply_to_id ON message_queue(reply_to_id);
                CREATE INDEX IF NOT EXISTS idx_message_queue_thread_id ON message_queue(thread_id);
            ";

            using var indexCommand = new SQLiteCommand(indexSchema, _connection);
            indexCommand.ExecuteNonQuery();
        }

        private void MigrateSchema()
        {
            // Add notification_type column if it doesn't exist
            try
            {
                const string addNotificationType = @"
                    ALTER TABLE message_queue ADD COLUMN notification_type TEXT DEFAULT 'message';
                ";
                using var cmd1 = new SQLiteCommand(addNotificationType, _connection);
                cmd1.ExecuteNonQuery();
            }
            catch (SQLiteException) { /* Column already exists */ }

            // Add task_id column if it doesn't exist
            try
            {
                const string addTaskId = @"
                    ALTER TABLE message_queue ADD COLUMN task_id TEXT;
                ";
                using var cmd2 = new SQLiteCommand(addTaskId, _connection);
                cmd2.ExecuteNonQuery();
            }
            catch (SQLiteException) { /* Column already exists */ }

            // Add task_title column if it doesn't exist
            try
            {
                const string addTaskTitle = @"
                    ALTER TABLE message_queue ADD COLUMN task_title TEXT;
                ";
                using var cmd3 = new SQLiteCommand(addTaskTitle, _connection);
                cmd3.ExecuteNonQuery();
            }
            catch (SQLiteException) { /* Column already exists */ }

            // Add reply_to_id column for threading support
            try
            {
                const string addReplyToId = @"
                    ALTER TABLE message_queue ADD COLUMN reply_to_id INTEGER;
                ";
                using var cmd4 = new SQLiteCommand(addReplyToId, _connection);
                cmd4.ExecuteNonQuery();
            }
            catch (SQLiteException) { /* Column already exists */ }

            // Add thread_id column for threading support
            try
            {
                const string addThreadId = @"
                    ALTER TABLE message_queue ADD COLUMN thread_id TEXT;
                ";
                using var cmd5 = new SQLiteCommand(addThreadId, _connection);
                cmd5.ExecuteNonQuery();
            }
            catch (SQLiteException) { /* Column already exists */ }
        }

        /// <summary>
        /// Enqueue a message for delivery.
        /// </summary>
        /// <param name="fromTerminal">Sender terminal name.</param>
        /// <param name="toTerminal">Recipient terminal name.</param>
        /// <param name="content">Message content.</param>
        /// <returns>The message ID.</returns>
        public long EnqueueMessage(string fromTerminal, string toTerminal, string content)
        {
            return EnqueueMessage(fromTerminal, toTerminal, content, "message", null, null);
        }

        /// <summary>
        /// Enqueue a message with notification type for delivery.
        /// </summary>
        /// <param name="fromTerminal">Sender terminal name.</param>
        /// <param name="toTerminal">Recipient terminal name.</param>
        /// <param name="content">Message content.</param>
        /// <param name="notificationType">Type: "message", "helper_added", "help_requested", "system".</param>
        /// <param name="taskId">Optional task ID for task-related notifications.</param>
        /// <param name="taskTitle">Optional task title for task-related notifications.</param>
        /// <returns>The message ID.</returns>
        public long EnqueueMessage(string fromTerminal, string toTerminal, string content,
            string notificationType, string taskId = null, string taskTitle = null)
        {
            return EnqueueMessage(fromTerminal, toTerminal, content, notificationType, taskId, taskTitle, null, null);
        }

        /// <summary>
        /// Enqueue a message with full threading support for delivery.
        /// </summary>
        /// <param name="fromTerminal">Sender terminal name.</param>
        /// <param name="toTerminal">Recipient terminal name.</param>
        /// <param name="content">Message content.</param>
        /// <param name="notificationType">Type: "message", "helper_added", "help_requested", "system".</param>
        /// <param name="taskId">Optional task ID for task-related notifications.</param>
        /// <param name="taskTitle">Optional task title for task-related notifications.</param>
        /// <param name="replyToId">Optional ID of message being replied to (for threading).</param>
        /// <param name="threadId">Optional thread identifier (for threading).</param>
        /// <returns>The message ID.</returns>
        public long EnqueueMessage(string fromTerminal, string toTerminal, string content,
            string notificationType, string taskId, string taskTitle, string replyToId, string threadId)
        {
            const string sql = @"
                INSERT INTO message_queue (from_terminal, to_terminal, content, status, created_at, notification_type, task_id, task_title, reply_to_id, thread_id)
                VALUES (@from, @to, @content, 'pending', @createdAt, @notificationType, @taskId, @taskTitle, @replyToId, @threadId);
                SELECT last_insert_rowid();
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@from", fromTerminal);
            command.Parameters.AddWithValue("@to", toTerminal);
            command.Parameters.AddWithValue("@content", content);
            command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);
            command.Parameters.AddWithValue("@notificationType", notificationType ?? "message");
            command.Parameters.AddWithValue("@taskId", (object)taskId ?? DBNull.Value);
            command.Parameters.AddWithValue("@taskTitle", (object)taskTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("@replyToId", (object)replyToId ?? DBNull.Value);
            command.Parameters.AddWithValue("@threadId", (object)threadId ?? DBNull.Value);

            var result = command.ExecuteScalar();
            return Convert.ToInt64(result);
        }

        /// <summary>
        /// Get all pending messages, optionally filtered by recipient.
        /// </summary>
        /// <param name="toTerminal">Optional recipient filter.</param>
        /// <param name="maxRetries">Maximum retry count to include (default 3).</param>
        /// <returns>List of pending messages.</returns>
        public List<QueuedMessage> GetPendingMessages(string toTerminal = null, int maxRetries = 3)
        {
            var messages = new List<QueuedMessage>();

            var sql = @"
                SELECT id, from_terminal, to_terminal, content, status, retry_count, created_at, error,
                       notification_type, task_id, task_title, reply_to_id, thread_id
                FROM message_queue
                WHERE status = 'pending' AND retry_count < @maxRetries
            ";

            if (!string.IsNullOrEmpty(toTerminal))
            {
                sql += " AND to_terminal = @to";
            }

            sql += " ORDER BY created_at ASC";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@maxRetries", maxRetries);
            if (!string.IsNullOrEmpty(toTerminal))
            {
                command.Parameters.AddWithValue("@to", toTerminal);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                messages.Add(new QueuedMessage
                {
                    Id = reader.GetInt64(0),
                    FromTerminal = reader.GetString(1),
                    ToTerminal = reader.GetString(2),
                    Content = reader.GetString(3),
                    Status = reader.GetString(4),
                    RetryCount = reader.GetInt32(5),
                    CreatedAt = reader.GetDateTime(6),
                    Error = reader.IsDBNull(7) ? null : reader.GetString(7),
                    NotificationType = reader.IsDBNull(8) ? "message" : reader.GetString(8),
                    TaskId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    TaskTitle = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ReplyToId = reader.IsDBNull(11) ? null : reader.GetString(11),
                    ThreadId = reader.IsDBNull(12) ? null : reader.GetString(12)
                });
            }

            return messages;
        }

        /// <summary>
        /// Mark a message as being delivered (in-flight).
        /// Prevents retry attempts while delivery is in progress.
        /// </summary>
        /// <param name="messageId">The message ID.</param>
        public void MarkDelivering(long messageId)
        {
            const string sql = @"
                UPDATE message_queue
                SET status = 'delivering'
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", messageId);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Mark a message as delivered.
        /// </summary>
        /// <param name="messageId">The message ID.</param>
        public void MarkDelivered(long messageId)
        {
            const string sql = @"
                UPDATE message_queue
                SET status = 'delivered', delivered_at = @deliveredAt
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", messageId);
            command.Parameters.AddWithValue("@deliveredAt", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Mark a message as failed and increment retry count.
        /// </summary>
        /// <param name="messageId">The message ID.</param>
        /// <param name="error">Error description.</param>
        public void MarkFailed(long messageId, string error = null)
        {
            const string sql = @"
                UPDATE message_queue
                SET status = CASE WHEN retry_count >= 2 THEN 'failed' ELSE 'pending' END,
                    retry_count = retry_count + 1,
                    error = @error
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", messageId);
            command.Parameters.AddWithValue("@error", (object)error ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Reset stale "delivering" messages back to "pending" for retry.
        /// Called by ProcessPendingMessages to handle messages stuck in "delivering" state.
        /// </summary>
        /// <param name="timeoutSeconds">Messages in "delivering" state longer than this are reset (default 30).</param>
        /// <returns>Number of messages reset.</returns>
        public int ResetStaleDeliveringMessages(int timeoutSeconds = 30)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-timeoutSeconds);

            const string sql = @"
                UPDATE message_queue
                SET status = 'pending',
                    error = 'Reset from stale delivering state after timeout'
                WHERE status = 'delivering' AND created_at < @cutoff
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@cutoff", cutoff);
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get a message by ID.
        /// </summary>
        public QueuedMessage GetMessage(long messageId)
        {
            const string sql = @"
                SELECT id, from_terminal, to_terminal, content, status, retry_count, created_at, delivered_at, error,
                       notification_type, task_id, task_title
                FROM message_queue
                WHERE id = @id
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@id", messageId);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new QueuedMessage
                {
                    Id = reader.GetInt64(0),
                    FromTerminal = reader.GetString(1),
                    ToTerminal = reader.GetString(2),
                    Content = reader.GetString(3),
                    Status = reader.GetString(4),
                    RetryCount = reader.GetInt32(5),
                    CreatedAt = reader.GetDateTime(6),
                    DeliveredAt = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    Error = reader.IsDBNull(8) ? null : reader.GetString(8),
                    NotificationType = reader.IsDBNull(9) ? "message" : reader.GetString(9),
                    TaskId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    TaskTitle = reader.IsDBNull(11) ? null : reader.GetString(11)
                };
            }

            return null;
        }

        /// <summary>
        /// Clean up old delivered messages (older than specified hours).
        /// </summary>
        /// <param name="olderThanHours">Delete messages older than this many hours (default 24).</param>
        /// <returns>Number of messages deleted.</returns>
        public int CleanupOldMessages(int olderThanHours = 24)
        {
            var cutoff = DateTime.UtcNow.AddHours(-olderThanHours);

            const string sql = @"
                DELETE FROM message_queue
                WHERE status = 'delivered' AND delivered_at < @cutoff
            ";

            using var command = new SQLiteCommand(sql, _connection);
            command.Parameters.AddWithValue("@cutoff", cutoff);
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// Get count of pending messages by terminal.
        /// </summary>
        public Dictionary<string, int> GetPendingCounts()
        {
            var counts = new Dictionary<string, int>();

            const string sql = @"
                SELECT to_terminal, COUNT(*) as count
                FROM message_queue
                WHERE status = 'pending'
                GROUP BY to_terminal
            ";

            using var command = new SQLiteCommand(sql, _connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }

            return counts;
        }

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
    /// Represents a message in the queue.
    /// </summary>
    public class QueuedMessage
    {
        public long Id { get; set; }
        public string FromTerminal { get; set; }
        public string ToTerminal { get; set; }
        public string Content { get; set; }
        public string Status { get; set; }
        public int RetryCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// Notification type for two-tier helper notifications.
        /// Values: "message" (default), "helper_added" (Tier 1), "help_requested" (Tier 2), "system"
        /// </summary>
        public string NotificationType { get; set; } = "message";

        /// <summary>
        /// Optional task ID for task-related notifications.
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Optional task title for task-related notifications.
        /// </summary>
        public string TaskTitle { get; set; }

        /// <summary>
        /// ID of the message this is replying to (for threading support).
        /// </summary>
        public string ReplyToId { get; set; }

        /// <summary>
        /// Thread identifier for grouping related messages.
        /// </summary>
        public string ThreadId { get; set; }
    }
}
