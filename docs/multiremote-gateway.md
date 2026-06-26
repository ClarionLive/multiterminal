# MultiRemote Phone Gateway (in-process, Phase 2)

Task `ca6c5344` folded the standalone MultiRemote/ClaudeRemote phone gateway **into the
MultiTerminal process**. MT now hosts the phone PWA + API itself on a second in-process
Kestrel listener (default `:5100`, loopback-only), fronted by Tailscale serve. There is no
longer a separate `dotnet` companion or Caddy.

Source: `API/Gateway/` (host: `MultiRemoteGatewayHost.cs`).

## Hosting model

- A second, independent `WebApplication` on `:5100` (loopback). Keeps its own pipeline
  (ForwardedHeaders → security headers → WebSockets → session → auth → static PWA →
  in-process API) so it never contaminates MT's unauthenticated `:5050` API (DECISION D1).
- The proxy hops the standalone made to `:5050` are gone — the gateway calls MT's services
  **in-process**. Most routes are served by a whitelisted subset of MT's own controllers
  mounted in the gateway (tasks/projects/team/remote-mode/digest); the rest are hand-mapped
  onto `MessageBroker`/`SpawnService`/`TerminalStreamService`. The terminal console
  WebSocket binds straight to `TerminalStreamService`; the inbox→push monitor reads MT's
  inbox directly instead of HTTP-polling. The only remaining outbound hop is the off-box
  Cloudflare permission relay.

## Setup (recommended: the Multi-Connect tab + skill)

The **primary, self-service way** to configure phone connectivity is in-app — no hand-editing
JSON:

1. **Settings → Multi-Connect tab.** Set the gateway port, Tailscale hostname/serve port,
   phone-login username/password, and the relay/push fields. Each field shows its **effective
   value and source** (settings / appsettings / default) so you never edit into a shadow.
   `Detect` fills the Tailscale hostname, `Test connection` probes the gateway `/health`, and
   `Copy` grabs the phone URL. Per-install values are stored settings-first (in `settings.txt`;
   the three secrets — phone password, relay ApiKey, push NotificationSecret — are
   DPAPI-protected at rest). Clicking **OK** restarts the gateway in-process so restart-required
   fields (port, VapidSubject, NotificationSecret, relay BaseUrl) take effect without relaunching
   MT.
2. **`/multi-connect-setup` skill.** Installs/configures Tailscale (only the browser login is
   manual), runs `tailscale serve`, detects the hostname, writes the values back via the
   loopback `POST /api/multi-connect/config`, verifies `/health`, and prints the phone URL.

Hand-editing `appsettings.Local.json` (below) is the **fallback/advanced route** for headless or
scripted installs.

## Configuration keys (resolution order)

Each per-install value resolves **settings.txt (Multi-Connect tab) → `appsettings.Local.json` →
committed `appsettings.json` → built-in default**. Secrets and per-install overrides belong in
the tab (settings.txt) or in gitignored `appsettings.Local.json` (see
`appsettings.Local.json.example`) — never in the committed `appsettings.json`, which now ships
with **neutral placeholders** (no per-owner host/identity/relay baked in).

| Key | Committed default | Purpose |
|-----|---------|---------|
| `MultiRemote:Port` | `5100` | Gateway listen port (loopback). **Keep `tailscale serve` aligned to this.** |
| `MultiRemote:DataPath` | `%APPDATA%\MultiTerminal` | Dir for `push-config.json` + `notification-toggles.json`. |
| `MultiRemote:VapidSubject` | *(unset → code fallback `mailto:admin@localhost`)* | VAPID `sub` claim (the PWA origin). Set per-install in the tab or Local. |
| `MultiRemote:InboxUserId` | `Owner` | Whose inbox the push monitor watches (the inbox is keyed by this id). Set to **your MT identity name** in the tab/Local if your inbox key differs (e.g. an existing install keyed under a personal name). |
| `MultiRemote:NotificationSecret` | `""` (unauth) | Shared secret for `/api/notifications/runtime`. When set, MT's in-process forwarder automatically attaches the matching `X-MT-Secret` (via `GatewayRuntimeConfig`), so setting it secures the endpoint end-to-end without breaking push. Empty = unauthenticated (loopback/tailnet-only). **Set in the tab/Local to secure.** |
| `MultiRemote:PermissionRelay:BaseUrl` | *(unset → code fallback to the shared relay)* | Cloudflare permission relay base. Set your own in the tab/Local. |
| `MultiRemote:PermissionRelay:ApiKey` | `""` | Relay `X-API-Key`. **Set in the tab/Local.** |
| `MultiRemote:Auth:Username` / `:Password` | `changeme` / `changeme` | Phone login. **Set in the tab/Local** (login is disabled until both are non-default). |
| `MultiRemote:Auth:SessionTimeoutMinutes` | `1440` | Session cookie idle timeout. |
| `MultiRemote:Auth:CookieName` | `MultiRemoteSession` | Session cookie name. |
| `AllowedHosts` | `*.ts.net;localhost;127.0.0.1;[::1]` | Trimmed per DECISION D2 (Caddy/DDNS dropped). |

## Migration / deploy steps (one-time per install)

1. **Preserve push identity:** copy the standalone's `push-config.json` (VAPID keys + Apple
   subscriptions) into `MultiRemote:DataPath` (default `%APPDATA%\MultiTerminal`). If you
   skip this the gateway generates fresh VAPID keys and every phone must re-subscribe.
2. **Secrets (REQUIRED for login):** set `Auth`, `NotificationSecret`, and the relay/push
   fields in the **Settings → Multi-Connect tab** (preferred — stored settings-first, secrets
   DPAPI-protected) or, for headless/scripted installs, copy `appsettings.Local.json.example` →
   `appsettings.Local.json` next to the exe and fill them in there. **Login is disabled (503)
   until both `Auth:Username` and `Auth:Password` are set to non-default values** — the gateway
   fails closed rather than accept the committed `changeme/changeme` placeholders behind
   Tailscale.
3. **Tailscale:** `tailscale serve` must terminate TLS and forward to `http://localhost:5100`
   (the gateway trusts `X-Forwarded-Proto=https` from loopback → issues Secure cookies).
   Verify it persists across reboot (`tailscale serve --bg`) and that
   `https://<host>.ts.net/health` returns `{"status":"healthy"}` from the in-process host.
4. **Retire the standalone:** the separate MultiRemote `dotnet` companion is **quarantined**,
   not deleted, until end-to-end PM sign-off (HTTPS login, Web Push, terminal console WS, task
   board, chat — all from the phone). Current retirement state (task `ca6c5344`, item [10]):
   - The `ClaudeRemote` entry in `companion-processes.json` is set `autoStart:false` (one-flip
     back-out; original preserved in `companion-processes.json.bak-ca6c5344`). It is **not**
     removed yet — removal happens *after* sign-off so the rollback stays cheap.
   - The standalone project (`H:\DevLaptop\Projects\ClaudeRemote`) is left intact and runnable,
     with a `RETIRED.md` marker documenting the supersession + the back-out procedure.
   - Free `:5100` first (stop the in-process gateway) before running the standalone — both bind
     that port.

   **End-to-end status (2026-06-13):** all 6 phone tests PASS over Tailscale. Test 6
   (inbox/runtime push) root cause was iOS **Focus mode** silencing the PWA banner, not a code
   defect — resolved by allowlisting MultiRemote in the device's Focus settings; server path
   verified healthy (gateway `delivered:true`, events-store record confirmed). **After sign-off:**
   remove the `ClaudeRemote` companion entry entirely and archive the standalone folder.

## Re-enabling `NotificationSecret` (runbook)

`MultiRemote:NotificationSecret` is read **once at gateway startup** and published to the
in-process `GatewayRuntimeConfig` bridge, which MT's `NotificationsController` reads to attach the
matching `X-MT-Secret` on every forward (both the hook path and the MCP `send_push_notification`
tool, which routes through MT `:5050` with `forcePush=true` — task `ca6c5344`, item [11]). Because
it's a startup-time read, there is an ordering contract:

1. Set `MultiRemote:NotificationSecret` to a non-empty value in `appsettings.Local.json` (next to
   the exe). Use the same value the gateway and forwarder share — they're the same process, so one
   key serves both.
2. **Restart MT** so the gateway's `StartAsync` republishes the secret to `GatewayRuntimeConfig`.
   Editing the file while MT is running does **not** take effect — the in-memory value and the file
   will diverge and every push will 403 until restart.
3. If the MCP `index.js` changed (item [11]), also **restart the `multiterminal` MCP server**
   (re-launch the Claude Code session, or restart the MCP host) so the new `send_push_notification`
   handler loads.
4. Verify: `send_push_notification` returns `✅ Push delivered to N/M device(s)` (no 403), and a
   hook-path notification with remote-mode ON still reaches the phone.

If the gateway hasn't published its config yet (still starting, disabled, or failed to bind), the
forwarder falls back to the `:5100` default with an **empty** secret — so a push fired in that
window reports `gateway-unreachable` or `gateway-error:403`. That's expected during startup; retry
after the gateway is up rather than hammering the 100/min rate limiter.
