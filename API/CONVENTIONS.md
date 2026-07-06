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
