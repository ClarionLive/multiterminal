using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents a file attachment on a task or checklist item.
    /// Attachments are stored on disk with metadata persisted in SQLite.
    /// </summary>
    public class TaskAttachment
    {
        /// <summary>
        /// Unique attachment identifier.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>
        /// The task ID this attachment belongs to.
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Index of the checklist item this attachment is associated with.
        /// -1 indicates a task-level attachment (not tied to a specific checklist item).
        /// </summary>
        public int ChecklistItemIndex { get; set; } = -1;

        /// <summary>
        /// Original file name as uploaded by the user.
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// File name used for disk storage (includes attachment ID prefix to avoid collisions).
        /// </summary>
        public string StoredFileName { get; set; }

        /// <summary>
        /// MIME type of the attachment (e.g., "image/png", "image/jpeg").
        /// </summary>
        public string MimeType { get; set; }

        /// <summary>
        /// File size in bytes.
        /// </summary>
        public long FileSizeBytes { get; set; }

        /// <summary>
        /// Name of the user or terminal that added this attachment.
        /// </summary>
        public string AddedBy { get; set; }

        /// <summary>
        /// When the attachment was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Result of adding an attachment to a task.
    /// </summary>
    public class AddAttachmentResult
    {
        public bool Success { get; set; }
        public string AttachmentId { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Result of deleting an attachment from a task.
    /// </summary>
    public class DeleteAttachmentResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
