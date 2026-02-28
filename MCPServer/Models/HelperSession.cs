using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents an ephemeral native helper session spawned via Claude Code's agent teams feature.
    /// Helpers are temporary tactical workers that execute specific tasks and dissolve after completion.
    /// </summary>
    public class HelperSession
    {
        /// <summary>
        /// Unique helper session identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// Optional task ID this helper is working on.
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// The prompt/instructions given to the helper.
        /// </summary>
        public string Prompt { get; set; }

        /// <summary>
        /// Terminal name that spawned this helper.
        /// </summary>
        public string SpawnedBy { get; set; }

        /// <summary>
        /// When the helper was spawned.
        /// </summary>
        public DateTime SpawnedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the helper completed (or failed).
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// Helper lifecycle status: 'spawning', 'working', 'completed', 'failed'.
        /// </summary>
        public string Status { get; set; } = "spawning";
    }

    /// <summary>
    /// Represents a message from a helper session.
    /// </summary>
    public class HelperMessage
    {
        /// <summary>
        /// Unique message identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// The helper session ID this message belongs to.
        /// </summary>
        public string HelperId { get; set; }

        /// <summary>
        /// The message content.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// When the message was sent.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Result of spawning a helper.
    /// </summary>
    public class SpawnHelperResult
    {
        public bool Success { get; set; }
        public string HelperId { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of updating helper status.
    /// </summary>
    public class UpdateHelperStatusResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of logging a helper message.
    /// </summary>
    public class LogHelperMessageResult
    {
        public bool Success { get; set; }
        public string MessageId { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of getting active helpers.
    /// </summary>
    public class GetActiveHelpersResult
    {
        public bool Success { get; set; }
        public System.Collections.Generic.List<HelperSession> Helpers { get; set; }
        public string Error { get; set; }
    }
}
