# MessageBroker region-extraction pattern

Proven by ticket **e7e89f4b** (Kanban Tasks → `TaskService`) and **validated on a second region** by
**86f3fd21** (Profiles → `ProfileService`). Use this to peel the remaining regions off the ~9K-LOC
`MessageBroker` god-file one at a time. It is **refactor-by-relocation**: move method bodies, do not redesign.
The broker keeps its full public surface, so callers never change.

> **Second-region result (86f3fd21):** the Profile extraction built **0/0 on the first compile** (vs
> TaskService's 23 error-driven iterations) — a smaller region, but the clean first build is the payoff of
> applying the inventory gotchas below UP FRONT (the cross-region writers were found by census greps before
> the move, not by the compiler after). The one thing the template did NOT cover was cross-region *writers*
> of the moved cache; that gap is now closed as step 7. **Log-source retag convention** (both extractions did
> it, endorsed as "a real fix wearing a MINOR tag"): route the region's log calls through host `Log*` wrappers
> that stamp the NEW service name (`"ProfileService"`), so a relocated `DebugLogService?.Info("MessageBroker", …)`
> line stops misattributing to the broker after the move.

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
7. **Shared state that other regions still WRITE** (validated by 86f3fd21 — the profile cache is written by
   the terminal-registration region: auto-create-on-register, mark-offline-on-disconnect, team-lead flag).
   TaskService never hit this (its cache had only readers), so it was a template GAP. Rule: the cache still
   moves to the service (single-owner-per-cache is non-negotiable); expose the NARROW write primitives the
   outside region needs (`TryAddX`, a `TryGetX` returning the cached ref for in-place mutation) and rewire the
   broker-side writers to them. **Preserve the caller's exact semantics — do NOT redesign the external write
   into the clean write path** (that's a separate ticket, e.g. e1643ccc for registration); a relocation ticket
   moves the cache, it doesn't reform every site that touches it. Those rewires are all INSIDE `MessageBroker.cs`,
   so the zero-external-caller-changes property (git diff outside broker+new == empty) still holds.

## Inventory gotchas (learned from the TaskService extraction)

- **Grep for BOTH `_underscore` fields AND public-property collaborators.** A field-access grep
  (`\b_[a-z]\w*`) finds `_tasks`/`_worktrees` but MISSES DI-set collaborators exposed as PascalCase
  auto-properties (`public ActivityService ActivityService { get; set; }`, `SummaryService`,
  `ComplexityDetector`, `ChangelogService`, `DefaultInboxRecipient`). The task region called five of
  these with no underscore; they're outbound coupling too and belong on the host interface. Grep the
  region for `[A-Z]\w+\.` bare-call/property access as well, and cross-check against the broker's
  `public X Prop { get` declarations.
- **The compiler is your final inventory.** No static pass is guaranteed complete — do the relocation,
  build, and let each unresolved reference tell you what it is: (a) a host-interface member, (b) state
  that moves too, or (c) a using. The TaskService move built with 23 errors, all one coherent class
  (the missed PascalCase collaborators), and driving them to zero WAS the completeness proof. Trust the
  build-error list over any hand-enumeration.
- **Also grep the region's own privates for external callers.** A helper that lives in the region but
  is called from OTHER regions (e.g. `IsTemporaryAgent`, `BroadcastTaskUpdate`) either stays broker-side
  and is host-exposed, or moves and is made `public` so the outside callers reach it via `_service.`.

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
  `scripts/verify-writepath.mjs`) must be **extended to scan the new file too** (add it to `TARGETS`) — else
  the census goes silently green on an emptied broker. Add a negative fixture proving it still falsifies
  post-move. **And (86f3fd21 addendum):** if step 7 introduced a relocated cross-region-writer PRIMITIVE
  (e.g. `TryAddProfile`), allowlist it in the census (a `NAMED_BYPASS`, noting the ticket that later removes
  it) — otherwise the census fails on the primitive itself. Extending the census = new-file-in-TARGETS **plus**
  allowlist-the-relocated-primitive **plus** negative-fixture, not just the first.
- 3–5 `FooService` unit tests (real temp-SQLite DB + a stub `IFooServiceHost`) — the point of the split.
