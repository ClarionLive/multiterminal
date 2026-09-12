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
        /// When <see cref="OwnerPid"/> was last bound to this row (task c9285d2a, pipeline Run 2).
        /// <para>Exists so the pid lookup can order candidates by REGISTRATION recency.
        /// <see cref="LastActiveAt"/> looks like the same thing and is not: it is an activity clock,
        /// bumped by sending a message and by the ready handshake, on the SENDER's row. So one
        /// process holding two rows could see "newest wins" run backwards the moment the older row
        /// sent anything — and a channel server resolving after that flip would bind the stale
        /// identity, which is the very wrong-name-bound outcome the ordering exists to prevent.</para>
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? OwnerBoundAt { get; set; }

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

        /// <summary>
        /// CORROBORATED refusals of ONE (name, port) pair inside the current window — not a lifetime
        /// total, and not a running increment (task c9285d2a, item 10 cycle 1). Non-zero means push
        /// delivery is dead for this terminal RIGHT NOW, and nothing else will say so; the MAGNITUDE
        /// means much less than the fact of it being non-zero. It is ASSIGNED the ledger's count for
        /// the current run rather than incremented, so it CAN GO DOWN: when a window rolls over, a
        /// long-refused server's run restarts at 1 and this field drops with it. Reset to zero the
        /// moment a port report is accepted.
        /// <para>The motivating case is version skew: a channel server started before plugin commit
        /// 7875686 echoes no launch nonce, so its port report cannot prove origin against a row that MT
        /// created WITH a nonce. Every 30s heartbeat is refused identically, <see cref="ChannelPort"/>
        /// stays null, and the terminal keeps looking healthy — <see cref="IsConnected"/> is true and
        /// <c>get_messages</c> polling works perfectly, because polling does not use the port. The
        /// failure is invisible precisely where an agent would look for it.</para>
        /// <para>⚠️ THREE conditions, not one, and none of them subsumes the others. A refusal reaches
        /// this field only when it (1) carried a channelPort, (2) landed on a row that has no
        /// <see cref="ChannelPort"/> of its own, and (3) is not the first refusal of that (name, port)
        /// pair inside the corroboration window. Each was added because the previous set was shown to
        /// be insufficient by a real failure, so do not simplify this back:</para>
        /// <para>(1) alone counted name claims as dead channels. (2) was added by Run 4 after four
        /// gates independently found that a refusal is BY DEFINITION the case where the broker could
        /// not attribute the report to this row — and since a name claim always carries a port too
        /// (registerPortOnce sends name and port together), a second shell claiming a live name marked
        /// a HEALTHY terminal dead. (3) was added after live testing on 2026-09-09 showed (2) still
        /// fails during the seconds between a terminal's own registration and its first port report,
        /// when it legitimately holds no route: 7.0s measured for an MT-launched terminal, 2.4s and
        /// 2.1s for adopted ones. Any stranger's claim arriving in that window marked it dead, and
        /// permanently, because the reset needs an accepted port report and a healthy server's
        /// heartbeat is drift-gated so it never sends another.</para>
        /// <para>Corroboration works because the two cases differ in kind, not degree: a refused
        /// channel server never gets its port into the roster, so the drift gate never silences it and
        /// it re-reports the SAME port every ~30s forever, whereas a stranger's claim is one-shot. The
        /// cost is that a genuine dead channel is reported one heartbeat late. That is the right trade:
        /// a health field that fires when nothing is wrong trains its reader to ignore it.</para>
        /// <para>NOT JsonIgnore'd, unlike the nonce and pid: those are check values that must never
        /// leave the process, whereas this exists to be read. It is a count of failures, discloses
        /// nothing an attacker could present as proof, and a health signal nobody can see is the exact
        /// defect this field was added to fix.</para>
        /// </summary>
        public int ChannelPortRefusalCount { get; set; }

        /// <summary>
        /// When the most recent port report for this terminal was refused, or null if none ever was.
        /// Cleared alongside <see cref="ChannelPortRefusalCount"/> the moment a port report is accepted,
        /// so the pair always describes the CURRENT state rather than accumulating history: non-zero
        /// means the channel is dead right now. A health signal that cannot return to healthy would just
        /// be a second way to be wrong — a recovered terminal would read as broken forever.
        /// </summary>
        public DateTime? LastChannelPortRefusalAt { get; set; }
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
