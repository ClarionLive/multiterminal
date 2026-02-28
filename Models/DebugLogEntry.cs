using System;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents a debug log entry in the internal debug log queue.
    /// </summary>
    public class DebugLogEntry
    {
        /// <summary>
        /// When this log entry was created.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Source component (e.g., "MessageBroker", "MainForm", "DebugPanel").
        /// For OutputDebugString messages, this will be "OutputDebugString".
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Log level/severity.
        /// </summary>
        public DebugLogLevel Level { get; set; }

        /// <summary>
        /// The log message content.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Process ID that generated this message (0 for internal messages).
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// Process name that generated this message (null for internal messages).
        /// </summary>
        public string ProcessName { get; set; }

        /// <summary>
        /// Creates a new debug log entry for internal messages.
        /// </summary>
        public DebugLogEntry(string source, DebugLogLevel level, string message)
        {
            Timestamp = DateTime.Now;
            Source = source;
            Level = level;
            Message = message;
            ProcessId = 0;
            ProcessName = null;
        }

        /// <summary>
        /// Creates a new debug log entry with process information (for OutputDebugString).
        /// </summary>
        public DebugLogEntry(string source, DebugLogLevel level, string message, int processId, string processName)
        {
            Timestamp = DateTime.Now;
            Source = source;
            Level = level;
            Message = message;
            ProcessId = processId;
            ProcessName = processName;
        }

        /// <summary>
        /// Formats the entry for display (without timestamp).
        /// </summary>
        public override string ToString()
        {
            if (ProcessId > 0 && !string.IsNullOrEmpty(ProcessName))
            {
                return $"[{ProcessName} ({ProcessId})] {Level}: {Message}";
            }
            return $"[{Source}] {Level}: {Message}";
        }

        /// <summary>
        /// Formats the entry with timestamp for export.
        /// </summary>
        public string ToFullString()
        {
            if (ProcessId > 0 && !string.IsNullOrEmpty(ProcessName))
            {
                return $"{Timestamp:HH:mm:ss.fff} [{ProcessName} ({ProcessId})] {Level}: {Message}";
            }
            return $"{Timestamp:HH:mm:ss.fff} [{Source}] {Level}: {Message}";
        }
    }

    /// <summary>
    /// Debug log severity levels.
    /// </summary>
    public enum DebugLogLevel
    {
        Trace,   // Detailed trace information
        Info,    // Informational messages
        Warning, // Warning messages
        Error    // Error messages
    }
}
