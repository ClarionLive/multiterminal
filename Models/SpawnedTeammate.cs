using System;
using System.Diagnostics;

namespace MultiTerminal.Models
{
    /// <summary>
    /// Represents a programmatically spawned teammate terminal.
    /// </summary>
    public class SpawnedTeammate
    {
        /// <summary>
        /// Unique document ID (8-character hex) used for registration.
        /// </summary>
        public string DocId { get; set; }

        /// <summary>
        /// Display name of the teammate (e.g., "Alice", "Bob").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Agent type/role (e.g., "researcher", "implementer", "reviewer").
        /// </summary>
        public string AgentType { get; set; }

        /// <summary>
        /// Working directory the terminal was spawned in.
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// The spawned PowerShell process.
        /// </summary>
        public Process Process { get; set; }

        /// <summary>
        /// When the teammate was spawned.
        /// </summary>
        public DateTime SpawnedAt { get; set; }

        /// <summary>
        /// Whether the teammate has completed registration via MessageBroker.
        /// </summary>
        public bool IsRegistered { get; set; }

        /// <summary>
        /// Terminal ID assigned during registration (from MessageBroker).
        /// </summary>
        public string TerminalId { get; set; }

        /// <summary>
        /// Optional initial prompt sent to the teammate after registration.
        /// </summary>
        public string InitialPrompt { get; set; }

        /// <summary>
        /// HTTP port for the terminal's Claude Code Channel server.
        /// </summary>
        public int ChannelPort { get; set; }
    }
}
