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

**If you ever add a new per-tab obligation, prefer an interface over a chain.** The whole cost of
`0d72698a` was one missing subscription that nothing could detect.

Plus a `<Content Include>` with `PreserveNewest` in `MultiTerminal.csproj` for the panel's HTML —
without it the tab loads a blank WebView2 with no error.

Also note `TerminalDocument`'s **late-terminal-name** path: the broker commonly arrives before
`CustomTitle`, so a tab that resolves anything by agent name must hook
`UpdateTaskHudTerminalName()` as well as `SetMessageBroker()`, or it sits permanently on its
empty state for most terminals.

## The board doorway

A **Plan glyph on each kanban card** opens this tab pinned to that ticket. The route:

```
tasks-panel.html  openPlanGraph(taskId)      -> postMessage {type:'open_plan_graph', taskId, assignee}
TasksPanelControl case "open_plan_graph"     -> raises PlanGraphRequested
TasksPanelDocument                            -> forwards it
MainForm          OnPlanGraphRequested        -> picks a TerminalDocument
TerminalDocument  OpenPlanGraph(taskId)       -> SetTask(taskId) then SwitchToTabById("__graph__")
```

It deliberately does NOT go through `MessageBroker`. Broker events (`BrowserTabRequested`) exist to
carry requests in from **out of process** — MCP tools and REST. This one starts as a click in a
sibling dock window, so it follows the panel-to-`MainForm` UI event precedent (`InjectRequested`)
and leaves the ~5.5K-LOC broker alone.

Three decisions worth keeping:

- **The card sends the assignee it is displaying**, rather than the host re-reading the task. What
  the user clicked is what they saw; a task reassigned between render and click must not silently
  open a different agent's HUD.
- **Resolution is preference-ordered, not a lookup.** Assignee's terminal first; then
  `_lastActiveTerminal`, so an **unassigned** card still opens somewhere. Falling back is safe only
  because `SetTask` PINS the view behind a visible "pinned" badge that clicks back to following the
  active task — borrowing your own HUD to read someone else's ticket is a read, and it is visibly
  undoable. Prefer `_lastActiveTerminal` over `_dockPanel.ActiveDocument`: DockPanelSuite's
  `ActiveDocument` is unreliable with WebView2, and here the click itself came from a WebView2 panel.
- **The glyph is gated on the ticket having a checklist.** The tab's honest empty state is "This
  ticket has no checklist yet", and a doorway into an empty room reads as a broken feature rather
  than an absent one.

`SetTask` runs BEFORE the tab switch, or the tab appears still showing the previously-pinned ticket
and then swaps under the reader. `OpenPlanGraph` calls `Activate()` — unlike the inject paths, which
removed it to stop focus stealing: those are agent-initiated and unbidden, this is a human asking to
be taken somewhere, and a HUD tab that changed behind a hidden document looks like a dead click.

Every hop above is a **string contract across files that no compiler checks** — the same shape as
the Run-1 CRITICAL (PascalCase renderer vs camelCase view, clean build, 442 green tests).
`MultiTerminal.Tests/PlanGraphDoorwayTests.cs` pins them, including a cross-file check that every
field the host reads is a field the panel actually sends.

## Remaining gap

The **lifecycle board** (`TaskLifecycleBoardForm`, a separate window) has no equivalent glyph. Same
entry point, same event; only the publisher is missing.
