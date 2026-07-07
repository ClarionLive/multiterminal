# MultiTerminal REST API Conventions (:5050)

Standardized in task 7ce19175 (Eval P3). Builds on the ProblemDetails exception
middleware from c522764d (P2).

## Response contract

- **Success = raw resource.** Return the resource object or list directly
  (`Ok(resource)`), or `Ok()` / `NoContent()` for a pure acknowledgement.
  Do **not** wrap in a `{ success = true, ... }` envelope.
- **Error = ProblemDetails (RFC 7807).** Unhandled exceptions become 500
  ProblemDetails via the global `RestApiExceptionHandler`. For handled 4xx, return
  `Problem(...)` / `ValidationProblem(...)` / `NotFound()` — not an ad-hoc
  `{ error = "..." }` anonymous object.
- Clients distinguish success from failure by **HTTP status**, never by a body
  `success` flag.

## Data-access rule

- **Controllers call `MessageBroker`; the broker owns `TaskDatabase`.** Controllers
  must not read/write `TaskDatabase` (`_taskDb`) directly — add a thin broker
  pass-through instead.
- Documented exception: `AgentStatsController` — see its class-level comment for
  the rationale.

## Migration rule (when removing a `{success}` envelope)

**Unwrap, don't restructure.** Drop the `success` field but keep every *other*
field at the top level (`{success=true, id}` → `{ id }`). This is transparent to
the MCP layer (`mcp/index.js` reads named fields + HTTP status, never `.success`)
and to lenient PWA checks (`res.success !== false`). Update any strict consumer in
the **same commit** — see the consumer notes on ticket 7ce19175.

## Coverage

This convention is the contract for the whole `:5050` surface. Two sweeps completed it:
7ce19175 (P3) migrated the `{success}`-envelope controllers; 15e18626 (P3b) migrated the
remaining error-only controllers that still emitted ad-hoc `{ error }`.

**Migrated (P3, 7ce19175):** Spawn, Notifications, Tasks, TaskReports, AgentStats,
ProjectContext, RemoteMode, AgentPanels, BrowserTabs, CodeGraph, Elicitations, Knowledge,
Messaging, Office, Ripgrep, SessionLineage, SessionMemory, Wiki, XamlPreview, Terminals.

**Migrated (P3b, 15e18626):** BranchMetadata, Debug, Digest, Gateway, MultiConnect,
OwnerProfile, SourceControlAccounts, Team, Worktrees, PermissionRelayTest (DEBUG-only). All
handled 4xx/5xx returns now use `Problem(detail, statusCode)`.

The whole `:5050` controller surface now emits ProblemDetails for every handled error.
New controllers should follow this contract from the start.

### Falsifiable close (P3b)

The error-shape sweep is verifiable by grep: **zero anonymous `{ error }` objects returned
on an ERROR status** (`BadRequest`/`NotFound`/`Conflict`/`StatusCode(4xx|5xx, …)`) remain in
`API/Controllers/`, outside the accepted divergences below.

    rg 'new\s*\{\s*[Ee]rror\s*=' API/Controllers   # → 0 matches

### Accepted divergences (NOT error returns — do not "fix")

These are `error`-named fields inside **HTTP 200** bodies (status/warning data, not RFC 7807
error returns), plus the one deliberate `{success}` retention. They are out of scope for the
error-shape contract by construction (the contract governs error-STATUS responses only):

- **`MultiConnectController` POST /config** (2 sites) — 200-OK partial-success bodies
  `{ applied=false, restartRequired=true, error="settings persisted but restart…" }`. The
  `error` field is a human-readable warning; the operation *succeeded* (settings persisted),
  so it is a 200, not a 4xx. Renaming the field would be a consumer-visible contract change
  for no benefit (P3b decision, PM-ratified).
- **`MultiConnectController` GET /tailscale-status** — 200-OK body carries `error =
  status.Error`, the Tailscale probe's own error string as a data field.
- **`NotificationsController`** — 200-OK push-result body carries a per-device `error` field
  (the device's push failure reason as data).
- **Gateway `/api/spawn`** (`API/Gateway/GatewayServiceEndpoints.cs`) keeps `{ success=true }`
  because the phone `terminals.js` strictly checks `data.success` (documented 5ad9ced).
