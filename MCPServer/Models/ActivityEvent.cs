using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents an activity event that should be displayed in the Activity Panel feed.
    /// </summary>
    public class ActivityEvent : EventArgs
    {
        /// <summary>
        /// Unique ID for deduplication.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>
        /// The terminal/user that triggered the activity.
        /// </summary>
        public string Terminal { get; set; }

        /// <summary>
        /// Activity type: "task", "plan", "build", "learned", "decision", etc.
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// The specific action: "created", "completed", "claimed", "started", "failed", etc.
        /// </summary>
        public string Action { get; set; }

        /// <summary>
        /// Human-readable description of the activity.
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// When the activity occurred.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Optional related entity ID (task ID, plan ID, etc.).
        /// </summary>
        public string RelatedId { get; set; }

        /// <summary>
        /// Optional additional details.
        /// </summary>
        public string Details { get; set; }
    }
}
