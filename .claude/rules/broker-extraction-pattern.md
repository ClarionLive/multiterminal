# MessageBroker region-extraction pattern

Proven by ticket **e7e89f4b** (Kanban Tasks → `TaskService`). Use this to peel the remaining regions
off the ~9K-LOC `MessageBroker` god-file one at a time. It is **refactor-by-relocation**: move method
bodies, do not redesign. The broker keeps its full public surface, so callers never change.

## The recipe

1. **Pick one `#region`.** Inventory it FIRST: method list, the state it owns (caches/locks), and every
   call it makes to code in *another* region (the "outbound coupling"). Decide per-method move/stay before
   touching code — cross-region callers are discovered here, not mid-move.
2. **Move to a new `FooService.cs`** (`internal sealed`, in `MCPServer/Services/`): the region's caches +
   locks + all its methods (bodies verbatim). The service takes `(TaskDatabase db, IFooServiceHost host)`.
3. **Broker keeps the public surface as one-line delegations:** `public X Foo(...) => _fooService.Foo(...);`
   (drop `async` on the delegation; the service keeps it). Private helpers just move (no stub). ZERO caller
   changes — controllers/panels/MainForm untouched.
4. **Events STAY declared on the broker** (subscribers unchanged). The service raises them through host
   wrappers (`RaiseXxx(...)`) that call the broker's private `RaiseSafe` — resilient dispatch is preserved.
5. **`IFooServiceHost`** is a NARROW interface, broker-implemented, listing EXACTLY the region's outbound
   coupling (event raisers, other-region utilities, DI-set collaborators, worktree lifecycle). Members that
   are already public broker methods auto-implement; private/new ones get explicit impls.
6. **Shared state that other regions still read** (e.g. `_tasks` read by the project/attachment regions):
   expose read accessors on the service and rewire those broker-side readers to `_service.GetX(...)`.

## What stays on the broker

Anything owned by a DIFFERENT region: its cache (e.g. `_projects` — one owner per cache, per bb2b0104),
its utilities, and cross-cutting collaborators (worktree lifecycle, activity/inbox, logging, DI-set
services). The service reaches them through the host. When that region extracts later, only the host
*implementation* changes — the service is untouched. That composability is the whole point.

## Wiring decision (locked): narrow host interface, NOT a raw broker back-reference

A raw `MessageBroker` reference passed to the service would re-expose all ~246 broker methods and make the
decomposition **cosmetic**. The narrow `IFooServiceHost` is the reviewable census of the region's coupling
(same "enumerate, don't prose" discipline as the write-path/DB-gate verifiers) and is what lets each future
region get its own slice. Rejected: raw back-ref (cosmetic, unbounded coupling).

## Falsifiable acceptance (every extraction)

- `git diff` outside `MessageBroker.cs` + the new files == empty (zero caller changes — the observable form
  of "every broker method is a one-line delegation").
- Build 0/0. Any invariant verifier that scanned `MessageBroker.cs` for the moved concern (e.g.
  `scripts/verify-writepath.mjs`) must be **extended to scan the new file too** — else the census goes
  silently green on an emptied broker. Add a negative fixture proving it still falsifies post-move.
- 3–5 `FooService` unit tests (real temp-SQLite DB + a stub `IFooServiceHost`) — the point of the split.
