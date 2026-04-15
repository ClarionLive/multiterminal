using System;
using System.Text.Json;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// A pending MCP elicitation request forwarded to ClaudeRemote for mobile input.
    /// Stored in-memory (short-lived, auto-expires after 5 minutes).
    /// </summary>
    public class ElicitationRequest
    {
        public string ElicitationId { get; set; }
        public string AgentName { get; set; }
        public string McpServerName { get; set; }
        public string Message { get; set; }
        public string SchemaJson { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Response from ClaudeRemote after user fills out the elicitation form.
    /// </summary>
    public class ElicitationResponse
    {
        public string Action { get; set; } // "accept", "decline", "cancel"
        public string ContentJson { get; set; } // JSON object with form field values
        public DateTime AnsweredAt { get; set; } = DateTime.UtcNow;
    }
}
