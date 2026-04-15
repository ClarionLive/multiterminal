using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Represents an image sent via chat message, stored as base64 in SQLite.
    /// Images are grouped by batch_id (multiple images sent in one message).
    /// </summary>
    public class MessageImage
    {
        public string Id { get; set; }
        public string BatchId { get; set; }
        public string FileName { get; set; }
        public string MimeType { get; set; }
        public string Base64Data { get; set; }
        public int FileSizeBytes { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Input model for saving a message image (before ID/batch assignment).
    /// </summary>
    public class MessageImageInput
    {
        public string FileName { get; set; }
        public string MimeType { get; set; }
        public string Base64Data { get; set; }
        public int FileSizeBytes { get; set; }
    }
}
