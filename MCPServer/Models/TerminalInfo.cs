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
        /// Per-launch proof-of-origin nonce (task fd3437e6). For an MT-seeded "Unassigned"
        /// placeholder this is the authoritative value MT injected into the real child's env;
        /// for any other registration it is the value the registrant echoed back. A registration
        /// may only adopt+promote an "Unassigned" placeholder when its echoed nonce matches the
        /// placeholder's seeded nonce — closing the docId-inheritance identity-hijack vector.
        /// Empty when unseeded; the adoption gate fails open in that (in-version unreachable) case.
        /// <para>SECURITY: this is a SECRET and MUST NEVER be serialized to any client. It is
        /// <see cref="System.Text.Json.Serialization.JsonIgnore"/>d so <c>GetTerminals()</c> /
        /// <c>GET /api/messaging/terminals</c> / the gateway <c>/api/terminals</c> / the MCP
        /// <c>list_terminals</c> tool cannot disclose it — otherwise any agent that can list
        /// terminals could read the nonce and replay it, collapsing proof-of-origin into a bearer
        /// token (codex-security-auditor A01/CWE-200). In-process gate reads (broker adoption
        /// branch, MainForm promote guard, the TerminalRegistered event) are unaffected — the
        /// attribute only suppresses JSON serialization, not property access.</para>
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string LaunchNonce { get; set; }

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
