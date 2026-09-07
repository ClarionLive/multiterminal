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
        /// PID of the Claude Code process this terminal's registration came from (task c9285d2a).
        /// <para>This exists because an ADOPTED terminal — one MT did not launch, registered by name
        /// from a plain shell — has no <see cref="LaunchNonce"/> to present, and so would fall through
        /// the duplicate-name gate's unseeded fail-open path. Its two processes (the MCP server that
        /// serves register_terminal, and the channel server that reports the port) share no secret, but
        /// they ARE siblings under one claude.exe, so the parent pid is the one thing both can state
        /// and a foreign process cannot state truthfully.</para>
        /// <para>HONEST LIMIT: a pid is a small enumerable integer, so this is materially WEAKER than
        /// the 32-char nonce and is accident-prevention (a stray session claiming a live name), not a
        /// defence against a determined local process. That is consistent with the surface it sits on:
        /// the REST API binds loopback and has no auth boundary by design, so any local process can
        /// already post anything.</para>
        /// <para>⚠️ SCOPE, CORRECTED BY PIPELINE RUN 1. An earlier version of this comment claimed the
        /// pid "never WEAKENS an MT-launched terminal, which is still held by its nonce". That was
        /// FALSE of the code: the gate OR'd the pid proof with the nonce proof, and every registration
        /// stamps an OwnerPid, so any row carrying a pid could be claimed by presenting that pid
        /// instead of the nonce — and <c>GET /api/messaging/channel-identity?ppid=</c> let a caller
        /// sweep pids to find which one held a name. Three reviewers flagged it; the Owner ruled the
        /// pid is DISCOVERY METADATA, NOT AUTHORIZATION.</para>
        /// <para>So the pid now proves origin ONLY for a row with no <see cref="LaunchNonce"/> — an
        /// adopted session, which has no nonce to present and previously had no check at all. A row
        /// that carries a nonce is held by that nonce and by nothing else. See
        /// <c>MessageBroker.RegisterTerminal</c> gate (4).</para>
        /// <para>⚠️ RESIDUAL RISK, STATED PLAINLY SO NOBODY RE-DISCOVERS IT AS A SURPRISE. Pairing the
        /// pid with a start time defeats pid RECYCLING, not pid ASSERTION. Both sides of that
        /// comparison are derived by the broker from the live process, so a local process that simply
        /// knows an adopted session's owning pid can still present it and be believed. Adopted rows
        /// are therefore protected at the "accident-prevention" level the original comment claimed for
        /// everything — a stray session cannot blunder into a live name — and not above it. Closing
        /// this properly needs a real shared secret between the MCP server and the channel server,
        /// which today share none; that is why the Owner scoped the pid to discovery rather than
        /// removing it. MT-launched terminals are unaffected: they are held by the nonce.</para>
        /// <para>SECURITY: JsonIgnore'd for the same reason as the nonce. It is a check value, and a
        /// listing that discloses it hands a caller the thing it would otherwise have to guess.</para>
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int? OwnerPid { get; set; }

        /// <summary>
        /// Start time of the process named by <see cref="OwnerPid"/>, captured when the pid was bound
        /// (task c9285d2a, pipeline Run 1).
        /// <para>A bare pid is not an identity — Windows recycles pids, and an adopted row is never
        /// marked disconnected (its session has no docId for <c>UnregisterTerminal</c> and no
        /// <c>MULTITERMINAL_NAME</c> for the SessionEnd hook), so a stale row can sit connected for
        /// MT's whole uptime. Without this field, an unrelated process that inherited the recycled pid
        /// would satisfy the pid check, adopt a live agent's name and repoint its channel port —
        /// silently receiving that agent's messages.</para>
        /// <para>(pid, start time) is the standard way to pin a pid to one specific process
        /// incarnation. It is also what lets the gate tell "held by a live owner" from "held by a
        /// corpse": a row whose owner is gone is treated as UNHELD and fails open, so a name is
        /// released when its session dies rather than burned until MT restarts.</para>
        /// <para>JsonIgnore'd alongside the pid — together they are the check value.</para>
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? OwnerStartTime { get; set; }

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
