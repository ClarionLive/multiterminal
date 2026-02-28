using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Notification type for two-tier helper assignment notifications.
    /// </summary>
    public enum NotificationType
    {
        /// <summary>
        /// Standard chat message (default).
        /// </summary>
        Message,

        /// <summary>
        /// Tier 1: Lightweight, non-interruptive FYI notification.
        /// Used when a terminal is added as a helper to a task.
        /// </summary>
        HelperAdded,

        /// <summary>
        /// Tier 2: Prominent, action-required notification.
        /// Used when help is actively requested from a terminal.
        /// </summary>
        HelpRequested,

        /// <summary>
        /// System notification (e.g., chat pause/resume).
        /// </summary>
        System
    }

    /// <summary>
    /// Represents a message sent between terminals.
    /// </summary>
    public class Message
    {
        /// <summary>
        /// Unique message identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Terminal ID or name of the sender.
        /// </summary>
        public string From { get; set; }

        /// <summary>
        /// Terminal ID or name of the recipient.
        /// </summary>
        public string To { get; set; }

        /// <summary>
        /// The message content.
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// When the message was sent.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether the message has been delivered to the recipient.
        /// </summary>
        public bool Delivered { get; set; }

        /// <summary>
        /// Whether this is a broadcast message.
        /// </summary>
        public bool IsBroadcast { get; set; }

        /// <summary>
        /// The type of notification. Determines UI rendering priority.
        /// </summary>
        public NotificationType NotificationType { get; set; } = NotificationType.Message;

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
        /// If this is the first message in a thread, ThreadId equals Id.
        /// </summary>
        public string ThreadId { get; set; }
    }

    /// <summary>
    /// Result of sending a message.
    /// </summary>
    public class SendResult
    {
        public bool Success { get; set; }
        public string MessageId { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of broadcasting a message.
    /// </summary>
    public class BroadcastResult
    {
        public bool Success { get; set; }
        public int RecipientCount { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of acknowledging a message delivery.
    /// </summary>
    public class AcknowledgeResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
