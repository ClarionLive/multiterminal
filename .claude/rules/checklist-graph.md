# Checklist dependency graph (🔗 Plan tab)

Task **60665c6c**. Renders a ticket's checklist as a dependency graph instead of a flat list,
with a plain-language explanation of each step on hover. Two audiences: the PM (see the
parallelism and branching a flat list hides) and the Owner (learn what a plan is doing rather
than approving it on trust).

## The one rule that matters

**Stored checklist array order is presentation order, NOT dependency.**

An item that declares no `dependsOn` gets **no incoming edge**. This is not a limitation to be
fixed later — it is the whole point. A renderer that draws array order as arrows invents a
constraint chain nobody declared, and then asserts it more confidently than the plan prose ever
did. A confidently wrong picture is worse than no picture.

`ChecklistGraphBuilder` enforces this, and
`ChecklistGraphBuilderTests.No_declared_dependencies_produces_no_edges` is its executable form.
**If that test ever goes red, the graph has started lying** — fix the builder, do not update the
test.

The same discipline applies to cross-task links: `related_to` carries no ordering, so it renders
as a floating node with no edges at all.

## Writing `dependsOn` and `gloss` (for planning agents)

Both live on the checklist item, so they flow through the existing `update_checklist` full-array
write path. Nothing new to call.

```json
{
  "item": "Extract TaskService",
  "status": "pending",
  "notes": [],
  "dependsOn": [0],
  "gloss": {
    "what": "Move the task code into its own file, leaving a one-line forwarder behind.",
    "why": "Gated on the inventory — the move is mechanical only once the decisions are made.",
    "without": "You find the coupling from compiler errors mid-move, when backing out is expensive."
  }
}
```

- `dependsOn` — zero-based **sibling** indices. Omit or leave empty when a step genuinely has no
  prerequisite. Do NOT chain items just because they are listed in order; that is the exact
  dishonesty this feature exists to remove.
- `gloss` — three short fields, no jargon. Deliberately separate from `notes`, which are
  reviewer-facing and full of internal vocabulary. **`without` is the one that teaches**: "what it
  is" describes the step, "without it" explains why anyone bothered.
- There is no `touched` field. Files come from `task_file_links` rows carrying a
  `checklist_item_index`, so that row is derived and always accurate. Call `link_task_file` with
  `checklistItemIndex` and it populates itself.

Bad declarations degrade rather than break: out-of-range indices, self-references, duplicates,
and cycles are each dropped with a warning shown in a banner above the graph. A malformed
dependency can never make a checklist unsaveable or take down the tab.

## Auto-backfilled gloss (task a455e295)

In practice almost nobody wrote a gloss, so the tab degraded into a prettier flat list. A
background agent now fills the gap: when a checklist is written and an item has text but no
gloss, one agent is spawned for the whole checklist (never one per item — step 3's *why* refers
to what step 2 produced, and an agent shown one item alone can only paraphrase it).

Three rules hold this together, and each exists because the obvious alternative is wrong:

- **`ChecklistItemGloss.Source` (`authored` | `generated`) is rendered.** A machine paraphrase
  reads exactly like the planner's reasoning while carrying no information, and a machine
  *invention* reads like it while being false. Unmarked, the reader approves a plan against a
  rationale no human endorsed. Absent/unrecognized normalizes to **authored**, never generated —
  every gloss predating the field was hand-written, and defaulting the other way would brand
  human work as machine output.
- **Backfill writes through `SetChecklistItemGloss`, never `update_checklist`.** The agent reads,
  thinks for minutes, then writes; a full-array replace carries that stale snapshot back over the
  live list and silently reverts any transition made meanwhile. `_checklistMutationLock` does not
  help — it serializes each call, not the thinking time between read and write.
  `TaskServiceTests.LostUpdate_IsExactlyWhatAFullArrayWriteDoes` performs the naive write and
  asserts the damage, so the reason is executable rather than folklore.
- **The trigger is the checklist WRITE, not task activation.** Activation looks like the natural
  hook and mostly no-ops: the standard flow is claim → in_progress → set_active → *then* plan, so
  at activation there is usually no checklist yet. Activation stays as a secondary sweep for the
  pre-feature backlog only.

Generated gloss is explicitly permitted to say *"the plan does not say what this is gated on."*
An honest gap invites the planner to fill it; a confident guess hides it forever.

| Env var | Default | Effect |
|---|---|---|
| `MULTITERMINAL_GLOSS_BACKFILL` | on | Master switch (`0`/`false`/`off`/`no`/`disabled` disable). Each run spawns a real agent that spends tokens — this is the off switch. |
| `MULTITERMINAL_GLOSS_BACKFILL_COOLDOWN_MS` | `600000` | Per-task cooldown, clamped to `[30000, 86400000]`. Stops the normal transition cadence from spawning a fleet. |

An in-flight set gives one run per task at a time, and the cooldown is stamped even on failure so
a broken spawn path retries on the cooldown rather than on every checklist edit.

## Where the pieces live

| Piece | File |
|-------|------|
| Model fields | `MCPServer/Models/Plan.cs` — `ChecklistItem.DependsOn`, `.Gloss`, `ChecklistItemGloss` |
| Graph builder (pure, static) | `Services/ChecklistGraphBuilder.cs` |
| Tests | `MultiTerminal.Tests/ChecklistGraphBuilderTests.cs` (17 facts) |
| REST | `GET /api/tasks/{taskId}/graph` in `API/Controllers/TasksController.cs` |
| HUD tab | `Controls/HudGraphPanel/HudGraphRenderer.cs` + `hud-graph.html` |
| Registration | `Docking/TerminalDocument.cs`, `Controls/HudTabContainer/HudTabContainer.cs` |
| Board HUD host | `TasksPanel/TasksPanelDocument.cs` — the second `HudTabContainer`, two tabs, bound to the selected card (task f5744489) |
| Board route tests | `MultiTerminal.Tests/BoardHudDoorwayTests.cs` |

The graph is **derived on every read** — from the live checklist, never stored. That is what makes
drift structurally impossible, and it is why the builder is pure: no broker, no DB, no UI.

## ⚠️ Trap for the NEXT HUD tab you add

`HudTabContainer.ApplyTheme()` is an `else if (tab.Control is XRenderer)` chain naming **every
renderer type explicitly**. A new tab that is not added to it silently never themes — no error, no
warning, it just quietly behaves differently from every other tab.

This is a **code shape, not a config list**. It has already bitten once, at full cost: the zoom side
used to be the same chain, and six of the seven renderers were never wired into the save half of it.
Every one of them raised `ZoomChanged` into a subscriber nobody had written, so zoom held for a
session and vanished on restart — for months, with nothing failing. That was ticket `0d72698a`.

**Zoom is no longer on this list, and that is the point.** `0d72698a` replaced the zoom chain with the
`IZoomableTab` interface (`Controls/IZoomableTab.cs`), so the container loops over a contract instead
of a hand-written type list. A tab that does not satisfy it **cannot be handed to the container at
all** — the omission is a compile error, not a bug report months later.
`MultiTerminal.Tests/ZoomableTabContractTests.cs` covers the half the compiler cannot: a renderer
copied from an existing one has both members and may still forget the interface.

**`ApplyTheme` was deliberately NOT converted** — separate concern, separate ticket. So the trap is
reduced, not removed, and theming is now the one place it still lives.

Registering a tab is three edits, not two:
1. Field + construction in `TerminalDocument`.
2. `AddPermanentTab("__id__", ...)` **and** add the id to `ReorderPermanentTabs(...)`.
3. `ApplyTheme` chain in `HudTabContainer`.

Zoom needs no edit at all — implement `IZoomableTab` (both members: `SetZoomFactor` **and**
`ZoomChanged`) and per-tab persistence, restore, and cross-terminal propagation follow for free.
Permanent tabs persist under their own tab id; dynamic browser tabs share one `__browser__` bucket.

⚠️ **There are now TWO `HudTabContainer` instances** (task f5744489): every terminal has one, and the
Tasks pane has its own two-tab board HUD. A tab added to the terminal container does NOT appear on
the board, and vice versa — decide which one you mean. If a tab should exist in both, it needs a
distinct id per container, because **the tab id is also the zoom persistence key** and a shared id
makes the two instances fight over one stored value.

**If you ever add a new per-tab obligation, prefer an interface over a chain.** The whole cost of
`0d72698a` was one missing subscription that nothing could detect.

Plus a `<Content Include>` with `PreserveNewest` in `MultiTerminal.csproj` for the panel's HTML —
without it the tab loads a blank WebView2 with no error.

Also note `TerminalDocument`'s **late-terminal-name** path: the broker commonly arrives before
`CustomTitle`, so a tab that resolves anything by agent name must hook
`UpdateTaskHudTerminalName()` as well as `SetMessageBroker()`, or it sits permanently on its
empty state for most terminals.

## The board reads its own tickets (task f5744489)

**The Tasks pane has its own HUD** — two tabs, Tasks and Plan — bound to the **selected card**.
Clicking a card selects it and fills that HUD. It opens no window and touches no terminal.

```
tasks-panel.html    selectTask(taskId)        -> postMessage {type:'card_selected', taskId}
TasksPanelControl   case "card_selected"      -> raises TaskSelected(taskId)
TasksPanelDocument  SetSelectedTask(taskId)   -> _boardTaskHud.SetTask + _boardGraph.SetTask
```

Three hops, all inside one panel. Nothing decides "whose HUD"; there is no such question.

### What this replaced, and why it is not coming back

There used to be a **Plan glyph** on each card that reached into a *terminal's* HUD and pinned it to
that ticket, resolving assignee's-terminal-else-`_lastActiveTerminal`. So an **unassigned** card
landed in whichever terminal the Owner last touched — chosen by accident, then displaying a ticket
it had no relationship to. This file defended that as "a read, and it is visibly undoable."

That defence conceded the point. Making a borrow reversible does not stop it being a borrow, and a
HUD that sometimes describes its own terminal and sometimes an arbitrary card is a category error
however good the undo is.

**Pinning is gone as a concept, not merely unused.** Both renderers now carry an explicit two-value
mode set at wiring time:

| Renderer | Terminal mode | Board mode |
|---|---|---|
| `HudGraphRenderer` | follows its terminal's active task; `SetTask` is **inert** | shows only the selected card; never falls back to a terminal |
| `TaskHudRenderer` | resolves its own active task; actions live | shows the selected card; **read-only** |

A terminal-mode renderer has no code path that binds it to a foreign ticket. The invariant is
structural, not conventional — which is the entire reason for preferring a mode over a nullable id.

### The Tasks tab is read-only on the board, and that is enforced in C#

`TaskHudRenderer` is **not a passive view**: `HandleSetTaskActive` claims tasks, re-assigns them
across agents behind a confirm dialog, and calls `SetTaskActive` — all expressed as "make this MY
active task". On the board there is no *my*.

So `HandleSetTaskActive` **refuses in board mode as its first statement**, ahead of the broker
null-check and every lookup. The view also hides the Activate button and the Not Active tab, but
that half is not trusted: the message arrives from a WebView2 and a stale one can reach the handler
regardless of the current DOM. `BoardHudDoorwayTests.Board_mode_refuses_activation_before_any_broker_call`
asserts the **ordering**, not just the presence — a guard placed after the first broker call would
already have acted for a terminal nobody chose.

Activation *from* the board is deliberately unimplemented rather than guessed at. "Activate for
whom?" needs an Owner decision, and answering it wrongly would reinstate the arbitrary-terminal
behaviour this ticket removed.

### Two traps worth knowing

- **Tab ids are zoom persistence keys.** The board HUD uses `__board_tasks__` / `__board_graph__`,
  NOT the terminal ids. This is mechanical, not stylistic: `MainForm` fans a changed zoom key out to
  every `TerminalDocument`, so sharing ids would make zooming the board's Plan tab silently resize
  the Plan tab in every open terminal. `HudTabContainer`'s constructor takes the tasks-tab id for
  exactly this reason.
- **Board mode must be set BEFORE `Initialize`.** `TaskHudRenderer.SetBoardMode` clears the queued
  terminal name; a name queued first would be adopted later by `Initialize` and quietly re-arm the
  action path. Both orderings are pinned by tests, because every visible symptom of getting this
  wrong looks correct.

### Empty states are three, not two

`no_task` carries `context` (`terminal` | `board`) and `hasSelection`, because "nothing is active",
"nothing is selected" and "the selected card was deleted" are three different truths. Collapsing the
last two would report a deletion as an idle state. All three sends route through one `NoTask()`
helper — the pre-f5744489 catch block hand-rolled its payload, omitted the flag, and announced "No
active task" on a tab that was showing someone else's ticket.

### Still a string contract

Every hop above is a **string contract across files that no compiler checks** — the same shape as
the Run-1 CRITICAL (PascalCase renderer vs camelCase view, clean build, 442 green tests).
`MultiTerminal.Tests/BoardHudDoorwayTests.cs` pins them, including a cross-file check that every
field the host reads is a field the panel actually sends, plus removal proofs that the old route is
absent rather than dormant. Two of its assertions were **demonstrated falsifiable** by reintroducing
the defect and watching them go red; the rest were not individually proven.

The lifecycle board is still reachable — from its own icon on the card, since the title now selects.
