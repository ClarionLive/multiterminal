using System;

namespace MultiTerminal.MCPServer.Models
{
    /// <summary>
    /// Information about a registered terminal.
    /// </summary>
    public class TerminalInfo
    {
        /// <summary>
        /// Unique identifier for the terminal.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Friendly name for the terminal (e.g., "Coder", "Tester").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// When the terminal registered.
        /// </summary>
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the terminal was last active.
        /// </summary>
        public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether the terminal is currently connected.
        /// </summary>
        public bool IsConnected { get; set; } = true;

        /// <summary>
        /// Color associated with this terminal in the chat UI.
        /// </summary>
        public string Color { get; set; }

        /// <summary>
        /// Document ID linking to the terminal tab in the UI.
        /// </summary>
        public string DocId { get; set; }

        /// <summary>
        /// Whether the agent has sent the ready confirmation (via webhook or message).
        /// Used for spawn handshake to ensure agent is initialized before sending work.
        /// </summary>
        public bool IsReady { get; set; } = false;

        /// <summary>
        /// HTTP port for the terminal's Claude Code Channel server.
        /// When set, messages are delivered via HTTP POST to localhost:{ChannelPort}/message
        /// instead of the legacy inbox file + [cm] nudge system.
        /// </summary>
        public int? ChannelPort { get; set; }
    }

    /// <summary>
    /// Result of registering a terminal.
    /// </summary>
    public class RegisterResult
    {
        public bool Success { get; set; }
        public string TerminalId { get; set; }
        public string Error { get; set; }
    }
}
