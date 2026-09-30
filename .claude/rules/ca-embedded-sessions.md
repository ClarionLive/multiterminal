# ClarionAssistant (CA) tabs: how an embedded session joins MT messaging

Ticket **9a731cda** (2026-09-29), follow-up to the channel retirement (0ff1b520). Read this before
changing registration, the credentials endpoint, `/api/health`, the plugin's dispatch filter, or
anything that keys on `CLARION_ASSISTANT_EMBEDDED`. CA depends on all of it and nothing in CA's
repo will fail when MT changes underneath it.

## What a CA tab is

A Claude Code session hosted by the Clarion IDE addin, **not** an MT pane. Launched as
`claude.exe -n 'CA-<slug>' --mcp-config <incl. the multiterminal MCP server> --plugin-dir <multiterminal> ...`
with `CLARION_ASSISTANT_EMBEDDED=1`, `MULTITERMINAL_NAME=CA-<slug>`, and **no**
`MULTITERMINAL_DOC_ID` / launch nonce. CA uniquifies names at launch.

**CA closes a tab by KILLING claude, and IDE exit kills every tab.** SessionEnd almost never fires.
The **liveness reaper is the primary release path** (30s sweep, `MULTITERMINAL_TERMINAL_REAP_MS`),
so a wrong ownerPid means a closed tab never leaves the roster.

## The contract (Owner decision: MESSAGING ONLY)

| Piece | Where | Rule |
|---|---|---|
| Registration | `mcp/index.js` `selfRegisterTerminal` CA branch | Only when `/api/health` carries MT's service marker (`multiterminal-rest-api`, `HealthIdentity.ServiceMarker`; a fingerprint, **not** authentication) AND its `capabilities` contains `ca-embedded-v1` AND `process.ppid`'s image is `claude.exe` (one `tasklist` lookup). Health unreachable / capability absent is **retried** (2s, 4s, 8s, 15s, 30s; ~1 min, unref'd timers) because MT may still be booting; a wrong marker, a non-claude parent or a refused register are final. Name-only + `ownerPid = process.ppid`, **awaited**; refused => no credential post. `EMBEDDED` (via `isClarionEmbedded`, mirrored in the plugin) **with** a DOC_ID still skips (the IDE inherited an MT shell's env and must not claim that pane). Never the `Unassigned` placeholder. |
| MT-pane self-registration | same function, case 1 | Registers the launch name with ownerPid, then **posts credentials** itself: the plugin's SessionStart post can reach MT before the row is connected (e.g. an Oracle crash-restart) and 409s, and nothing else retries. |
| Credentials | same branch, `postClaimedCredentials` | Posted once at MCP startup, with `ownerPid` (+ nonce when present). Valid for the tab's life: **`/clear` does not rotate the socket/token** (measured; same claude pid, new session id). |
| Capability | `/api/health` `capabilities`, `HostCapabilities` | `ca-embedded-v1` is present only while the reaper is actually running AND reaping clears credentials AND credential posts require owner proof. Runtime, not a version string: an MT with the reaper switched off must not advertise it. |
| Owner proof | `MessageBroker.TryStoreMessagingCredentials` (decides + stores atomically under `_registrationLock`) | Name must be a connected row. Pid-held row (owner bound, no nonce) needs the matching `ownerPid`; omitted => 409. Nonce-held row rejects a present-but-wrong nonce; an omitted nonce is still accepted for plugin compatibility (full route auth: c032a177 section D). |
| Disconnect | `MessagingController.DisconnectTerminal` | Never clears credentials itself; the broker's teardown compare-and-clears only when no live row still holds the name. |
| Plugin hooks | `dispatch-hook.js` `selectLeavesForEnv`, `CA_ALLOWED_LEAVES` | In a CA tab only `inbox-check-hook` runs. An **allowlist**: a leaf added later is off in CA by default. Standalone hooks (project-context, session-compact, the SessionStart echo) skip too. |
| session-status-hook | early return on `isClarionEmbedded` | Correct as-is: no output, no credential post (the MCP server's is authoritative), and **no SessionEnd disconnect** (by name it could tear down another IDE's same-named tab). |

## Accepted and documented gaps

- **Inbox fallback has no ownership check** (Owner decision). MT writes `<name>.json`; whoever runs
  under that name reads and deletes it. Two IDEs launching the same name at the same moment could
  cross-consume fallback messages. Native delivery is protected by the owner proof.
- **No MT safety-hook in CA tabs**, including its guard against killing MultiTerminal (messaging-only
  scope).
- `register_terminal("Bob")` inside a CA tab leaves `MULTITERMINAL_NAME=CA-x`, so Bob's inbox is not read.
- `CA-<slug>` profiles accumulate; broadcasts reach CA tabs.
- A register that times out after the broker accepted it leaves a row with no credentials
  (inbox-only) until the reaper removes it.
- A CA tab whose MCP server starts while MT is down for longer than the ~1 min retry window stays
  unregistered (inbox-only) until the tab is relaunched.
- **Local processes are trusted** (Owner decision, 2026-09-29, consistent with every other MT route).
  Deferred to c032a177 section D: a pid is not authentication (any local process can read it; an
  omitted nonce is still accepted for plugin compatibility), and no client authenticates the MT
  instance, so a process that binds :5050 before MT could collect credentials. Both apply to all
  terminals, not just CA.
- Every pane registration must carry its launch nonce (Oracle and "Launch as…" were fixed in this
  ticket): a pane row without one becomes pid-held once the MCP self-registration binds an owner, and
  the older plugin's SessionStart post (no ownerPid) would then be refused.
