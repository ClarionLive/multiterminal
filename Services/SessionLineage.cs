namespace MultiTerminal.Services
{
    /// <summary>
    /// Represents a session lineage record — one Claude Code session and its
    /// relationship to parent sessions and tasks.
    /// </summary>
    public class SessionLineageRecord
    {
        /// <summary>
        /// SQLite auto-increment primary key.
        /// </summary>
        public int DbId { get; set; }

        /// <summary>
        /// The Claude Code session GUID (e.g. "cefd392a-8736-4205-aa71-7e980662dd56").
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// The parent session's GUID. Null for root sessions.
        /// </summary>
        public string ParentSessionId { get; set; }

        /// <summary>
        /// The kanban task this session is linked to, if any.
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Name of the agent that ran this session (e.g. "Diana", "Bob").
        /// </summary>
        public string AgentName { get; set; }

        /// <summary>
        /// Category: "terminal", "subagent", or "sidechain".
        /// </summary>
        public string SessionType { get; set; } = "terminal";

        /// <summary>
        /// Optional human-readable summary of this session's work.
        /// </summary>
        public string Summary { get; set; }

        /// <summary>
        /// Absolute path to the JSONL file on disk for this session.
        /// </summary>
        public string SessionFilePath { get; set; }

        /// <summary>
        /// ISO 8601 timestamp of the first message in the session.
        /// </summary>
        public string StartedAt { get; set; }

        /// <summary>
        /// ISO 8601 timestamp of the last message in the session.
        /// </summary>
        public string EndedAt { get; set; }

        /// <summary>
        /// When this lineage record was inserted into the database.
        /// </summary>
        public string CreatedAt { get; set; }
    }

    /// <summary>
    /// Represents a single extracted message from a Claude Code session JSONL file.
    /// </summary>
    public class SessionMessageRecord
    {
        /// <summary>
        /// SQLite auto-increment primary key.
        /// </summary>
        public int DbId { get; set; }

        /// <summary>
        /// The session this message belongs to.
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// The kanban task this message is linked to, if any.
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Agent name for this message (same as the session's agent).
        /// </summary>
        public string AgentName { get; set; }

        /// <summary>
        /// Zero-based index of this message within the session.
        /// </summary>
        public int MessageIndex { get; set; }

        /// <summary>
        /// "user" or "assistant".
        /// </summary>
        public string Role { get; set; }

        /// <summary>
        /// Plain-text content of the message (tool call inputs/outputs are omitted or
        /// represented as a "[tool: ToolName]" stub when content arrays are flattened).
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// If this message represents a tool call, the tool name. Otherwise null.
        /// </summary>
        public string ToolName { get; set; }

        /// <summary>
        /// ISO 8601 timestamp from the JSONL file.
        /// </summary>
        public string Timestamp { get; set; }
    }
}
