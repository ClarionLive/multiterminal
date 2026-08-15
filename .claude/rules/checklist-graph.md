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

`HudTabContainer.ApplyTheme()` and `HudTabContainer.SetZoomFactor()` are each an
`else if (tab.Control is XRenderer)` chain naming **every renderer type explicitly**. A new tab
that is not added to **both** silently never themes and never zooms — no error, no warning, it
just quietly behaves differently from every other tab.

This is a **code shape, not a config list**, so it will bite again. Related: ticket `0d72698a`
persists per-tab zoom and enumerates tabs by name.

Registering a tab is four edits, not two:
1. Field + construction in `TerminalDocument`.
2. `AddPermanentTab("__id__", ...)` **and** add the id to `ReorderPermanentTabs(...)`.
3. `ApplyTheme` chain in `HudTabContainer`.
4. `SetZoomFactor` chain in `HudTabContainer`.

Plus a `<Content Include>` with `PreserveNewest` in `MultiTerminal.csproj` for the panel's HTML —
without it the tab loads a blank WebView2 with no error.

Also note `TerminalDocument`'s **late-terminal-name** path: the broker commonly arrives before
`CustomTitle`, so a tab that resolves anything by agent name must hook
`UpdateTaskHudTerminalName()` as well as `SetMessageBroker()`, or it sits permanently on its
empty state for most terminals.

## Known gap

The **board doorway** (a glyph on a task card that opens this tab focused on that ticket) is not
built. `HudGraphRenderer.SetTask(taskId)` is the entry point it needs; the missing piece is the
route from the Tasks panel / lifecycle board (separate dock windows) into a specific
`TerminalDocument`'s HUD. The established pattern to copy is `BrowserTabRequested` — a broker
event that `MainForm` handles by resolving a terminal id to its `TerminalDocument`
(`MainForm.cs:1355`).
