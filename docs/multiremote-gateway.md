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

## Configuration keys

Non-secret defaults live in committed `appsettings.json` (copied next to the exe). **Secrets
and per-install overrides go in `appsettings.Local.json`** (gitignored; see
`appsettings.Local.json.example`). `appsettings.Local.json` overrides `appsettings.json`.

| Key | Default | Purpose |
|-----|---------|---------|
| `MultiRemote:Port` | `5100` | Gateway listen port (loopback). **Keep `tailscale serve` aligned to this.** |
| `MultiRemote:DataPath` | `%APPDATA%\MultiTerminal` | Dir for `push-config.json` + `notification-toggles.json`. |
| `MultiRemote:VapidSubject` | `https://desktop.tail51f56.ts.net` | VAPID `sub` claim (the PWA origin). |
| `MultiRemote:InboxUserId` | `John` | Whose inbox the push monitor watches. |
| `MultiRemote:NotificationSecret` | `""` (unauth) | Shared secret for `/api/notifications/runtime`. When set, MT's in-process forwarder automatically attaches the matching `X-MT-Secret` (via `GatewayRuntimeConfig`), so setting it secures the endpoint end-to-end without breaking push. Empty = unauthenticated (loopback/tailnet-only). **Set in Local to secure.** |
| `MultiRemote:PermissionRelay:BaseUrl` | workers.dev URL | Cloudflare permission relay base. |
| `MultiRemote:PermissionRelay:ApiKey` | `""` | Relay `X-API-Key`. **Set in Local.** |
| `MultiRemote:Auth:Username` / `:Password` | `changeme` / `changeme` | Phone login. **Set in Local.** |
| `MultiRemote:Auth:SessionTimeoutMinutes` | `1440` | Session cookie idle timeout. |
| `MultiRemote:Auth:CookieName` | `MultiRemoteSession` | Session cookie name. |
| `AllowedHosts` | `*.ts.net;localhost;127.0.0.1;[::1]` | Trimmed per DECISION D2 (Caddy/DDNS dropped). |

## Migration / deploy steps (one-time per install)

1. **Preserve push identity:** copy the standalone's `push-config.json` (VAPID keys + Apple
   subscriptions) into `MultiRemote:DataPath` (default `%APPDATA%\MultiTerminal`). If you
   skip this the gateway generates fresh VAPID keys and every phone must re-subscribe.
2. **Secrets (REQUIRED for login):** copy `appsettings.Local.json.example` →
   `appsettings.Local.json` next to the exe and fill in `Auth`, `NotificationSecret`,
   `PermissionRelay:ApiKey`. **Login is disabled (503) until both `Auth:Username` and
   `Auth:Password` are set to non-default values** — the gateway fails closed rather than
   accept the committed `changeme/changeme` placeholders behind Tailscale.
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
