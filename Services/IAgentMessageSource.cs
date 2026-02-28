using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MultiTerminal.Models;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Common interface for agent message sources.
    /// Implemented by AgentProcess (piped stdin/stdout) and TranscriptTailer (file watching).
    /// Allows AgentPanelControl to display conversations from either source.
    /// </summary>
    public interface IAgentMessageSource
    {
        /// <summary>
        /// Fired when a new message is parsed from the source.
        /// </summary>
        event EventHandler<AgentMessage> MessageReceived;

        /// <summary>
        /// Fired when the source stops (process exits or file watching ends).
        /// Parameter is exit code (0 = normal, -1 = unknown/watcher stopped).
        /// </summary>
        event EventHandler<int> Stopped;

        /// <summary>
        /// Thread-safe read-only view of all received messages.
        /// </summary>
        IReadOnlyList<AgentMessage> Messages { get; }

        /// <summary>
        /// Whether the source is actively producing messages.
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Session ID from the message stream, if available.
        /// </summary>
        string SessionId { get; }

        /// <summary>
        /// Send a user message to the agent.
        /// For AgentProcess: writes to stdin.
        /// For TranscriptTailer: writes to the agent's inbox JSON file.
        /// </summary>
        Task SendMessageAsync(string content);

        /// <summary>
        /// Send an interrupt signal to the agent.
        /// For AgentProcess: sends interrupt control request.
        /// For TranscriptTailer: no-op (cannot interrupt native teammates).
        /// </summary>
        Task InterruptAsync();
    }
}
