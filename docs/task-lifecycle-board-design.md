# Task Lifecycle Board - Design Document

**Date:** 2026-02-10
**Authors:** Alice (brainstorm), Bob (research), PM (vision), Diana (backend review)
**Status:** APPROVED by PM (2026-02-10)
**Related Ticket:** `0e815093` (Make KanBan Form window resizable) + `32cc2235` (Implementation Checklist Grid)

---

## Vision

When you click "Edit" on a kanban card, instead of a cramped 6-tab modal, a **separate always-on-top floating window** opens that IS ITSELF a **mini kanban board** — where columns represent the lifecycle/workflow stages of that task.

Checklist items become **draggable cards** within those columns. The task's metadata lives in a compact header with a gear/settings popover. Notes are anchored to the phase they belong to.

### Market Validation (Bob's Research)

This concept is validated by existing tools:
- **Quire** — nested/recursive kanban boards
- **Kolan** — recursive board structures
- **Teamhood** — 2D kanban layouts
- **ClickUp** — users have explicitly requested multi-task pop-out windows

Nobody has combined all these ideas quite like this design.

---

## Current Pain Points

| Problem | Impact |
|---------|--------|
| 6 tabs hiding information | Can't see Plan and Implementation side by side |
| Fixed 1000px max-width | Not resizable, feels cramped |
| Checklist is a textarea | No visual workflow, no drag-and-drop |
| Window resizes on tab switch | Jarring UX, disorienting |
| Modal blocks main board | Can't reference the board while editing |

---

## Architecture Overview

### Window Type

- **Separate WinForms Form** (NOT a modal overlay)
- `TopMost = true` — always-on-top, floats above main board
- **Resizable** with `MinimumSize` (700x500) and sensible default (1100x700)
- Remembers position/size between uses (persist to settings)
- **Multiple windows can be open** simultaneously for different tasks
- Own WebView2 instance with dedicated HTML file

### Technology Stack

- **Window shell:** WinForms Form with WebView2 control
- **UI rendering:** HTML5 / CSS3 / JavaScript (same as main board)
- **Communication:** Existing C# ↔ JS message bridge pattern (`postMessage` / `ExecuteScriptAsync`)
- **Data model:** Reuses existing `ChecklistItem` model (Status/AssignedTo/CycleCount/Notes)
- **Drag-and-drop:** HTML5 native drag API (proven pattern from main board)

---

## Layout Design: "The Command Center"

```
┌──────────────────────────────────────────────────────────────────────┐
│ ⚙  "Implement Dark Mode"                    👤 Alice   📁 UI-Rework │
│    ■ urgent  ━━━━━━━━━━━━━━━━━━━━━━━░░░░░░ 68% complete    ─  □  ✕ │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  📋 Planning      │  🔨 Coding        │  🧪 Testing      │  ✅ Done  │
│  ────────────     │  ────────────     │  ────────────    │  ──────  │
│  ┌────────────┐   │  ┌────────────┐   │  ┌────────────┐  │ ┌──────┐│
│  │ Research    │   │  │ CSS vars   │   │  │ Light/dark  │  │ │ DB   ││
│  │ themes     │   │  │ system     │   │  │ toggle test │  │ │ migr ││
│  │ 📝 2 notes │   │  │ 🔄 1 cycle │   │  │ 👤 Diana    │  │ │  ✓   ││
│  └────────────┘   │  └────────────┘   │  └────────────┘  │ └──────┘│
│  ┌────────────┐   │  ┌────────────┐   │                  │ ┌──────┐│
│  │ Pick color  │   │  │ Component  │   │                  │ │ Scope││
│  │ palette    │   │  │ refactor   │   │                  │ │ doc  ││
│  └────────────┘   │  │ 👤 Alice   │   │                  │ │  ✓   ││
│                   │  └────────────┘   │                  │ └──────┘│
│  [+ Add Card]     │  [+ Add Card]     │                  │         │
│                   │                   │                  │         │
│  ┄ ┄ ┄ ┄ ┄ ┄ ┄   │  ┄ ┄ ┄ ┄ ┄ ┄ ┄   │  ┄ ┄ ┄ ┄ ┄ ┄ ┄  │         │
│  ▼ Phase Notes    │  ▼ Phase Notes    │  ▼ Phase Notes   │         │
│  "Evaluated 3     │  "Switched to     │  "Build passes,  │         │
│   theme libs..."  │   CSS custom      │   testing cross-  │         │
│                   │   properties..."  │   browser..."    │         │
├──────────────────────────────────────────────────────────────────────┤
│ 📌 Session Notes                                          [▼ Hide]  │
│ "Left off: CSS vars system 80% done. Next: wire up toggle │         │
│  component. Blocker: need design tokens from Figma file.  │         │
│  See card 'Component refactor' in Coding column."         │         │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Component Breakdown

### 1. Compact Header Bar

Replaces **Tab 1 (Overview)** and **Tab 6 (Metadata)**.

**Always visible elements:**
- **Task title** — Large font, inline-editable (click to edit)
- **Progress bar** — Auto-calculated from card positions across columns
- **Priority badge** — Color-coded (urgent=red, normal=blue, low=gray)
- **Assignee chip** — Shows avatar/name of primary assignee
- **Project chip** — Shows associated project

**Gear button (⚙) popover** — Click to open floating settings panel:
- Priority dropdown
- Assignee dropdown
- Project dropdown
- Helpers multi-select
- Description textarea (full task description)
- Read-only metadata: Task ID, Created By, Created At, Stale info, Sub-status

The gear popover keeps metadata **accessible but not cluttering** the main workspace.

### 2. Lifecycle Columns (The Mini Kanban)

**Fixed 4 columns** representing the workflow lifecycle (PM decision: no custom columns):

| Column | Maps To | Color Accent | Phase Notes Content |
|--------|---------|-------------|-------------------|
| 📋 Planning | `pending` status | Gray | Plan markdown (old Tab 2) |
| 🔨 Coding | `coding` status | Blue | Implementation summary (old Tab 3) |
| 🧪 Testing | `testing` status | Orange | Test results (old Tab 4) |
| ✅ Done | `done` status | Green | Completion/release notes (new!) |

**Column features:**
- Header with name, card count, and optional WIP limit indicator
- Vertical scrolling card area
- `[+ Add Card]` button at bottom
- Collapsible "Phase Notes" section at bottom of each column
- Drop target highlighting on drag-over

### 3. Sub-Task Cards (Enhanced Checklist Items)

Each card represents a `ChecklistItem` — the data model already exists!

**Card layout:**
```
┌──────────────────┐
│ CSS Variables     │  ← Item text (click to edit inline)
│ 🔄 2 cycles       │  ← CycleCount (testing→coding bouncebacks)
│ 👤 Alice          │  ← AssignedTo (dropdown on click)
│ 📝 3 notes        │  ← Notes[] count (expand to see history)
│ ▪▪▪░░             │  ← Visual age indicator (time in column)
└──────────────────┘
```

**Card interactions:**
- **Drag** between columns → updates `Status` field
- **Click card** → opens a **mini-modal within the lifecycle board** (not restricted by column width) for editing title, assignee, notes, and viewing full details
- **Click title** → inline edit (quick rename without opening modal)
- **Drag from Coding→Testing** → normal flow
- **Drag from Testing→Coding** → auto-increments `CycleCount` (bounceback tracking, already built!)
- **Right-click** → context menu (delete, duplicate, assign)

**Card aging (visual):**
- Cards gradually shift color the longer they sit in one column
- Fresh = white/default → 2 days = light yellow → 5 days = orange
- Subtle urgency signal without noise

### 4. Phase-Anchored Notes (The Tab Killer)

**This is the key innovation** that eliminates all 6 tabs.

Instead of separate tabs for Plan/Implementation/Testing/Continuation, each column has a **collapsible notes area** at the bottom:

- **Planning column** → Plan markdown (was Tab 2: "Planning")
- **Coding column** → Implementation summary (was Tab 3: "Implementation")
- **Testing column** → Test results (was Tab 4: "Testing")
- **Done column** → Completion/release notes (new field, or empty)

**Behavior:**
- Click the "Phase Notes" footer to expand/collapse
- **Multiple can be open simultaneously** (finally! side-by-side Plan + Implementation!)
- Markdown rendering with edit toggle
- Auto-save on blur
- Notes resize with the column

### 4b. Session Notes Strip (Persistent Bottom Bar)

> **Design change:** Continuation notes don't belong anchored to the Done column. They're session-level handoff notes ("where to pick up next"), written when work is IN PROGRESS across any column. They need to be the first thing the next person sees.

A **persistent collapsible strip** pinned to the bottom of the entire board, spanning all columns:

```
├──────────────────────────────────────────────────────────────────────┤
│ 📌 Session Notes                                          [▼ Hide]  │
│ "Left off: CSS vars system 80% done. Next: wire up toggle │         │
│  component. Blocker: need design tokens from Figma file."  │         │
└──────────────────────────────────────────────────────────────────────┘
```

**Behavior:**
- **Always visible by default** — collapsed to 1-2 lines, expandable to full height
- **Not tied to any column** — spans the full width of the board
- **First thing you see** when opening a task someone else worked on
- Markdown editing with auto-save
- Visual emphasis (subtle background tint, pinned icon) to distinguish from phase notes
- Maps to the existing `ContinuationNotes` field on KanbanTask (no new DB field needed)

### 5. Auto-Calculated Task Status

The parent task's status on the main board **derives from its children**:

| Child Card Distribution | Parent Status | Logic |
|------------------------|---------------|-------|
| No cards yet | *fall through to manual* | Nothing to derive from |
| All cards in Planning | `todo` | Planning phase |
| Any card in Coding or Testing | `in_progress` | Active work |
| All cards in Done | `done` | Complete! |

**Edge cases (Diana's review):**
- **Zero checklist items** → Fall through to manual status (nothing to derive from)
- **Manual override** → If user explicitly sets parent status on main board, respect it. Set `AutoStatus = false` on that task to prevent auto-recalculation overriding their choice
- **Default behavior** → `AutoStatus = true` for any task opened in the lifecycle board
- **Re-enable** → User can toggle auto-status back on via the gear menu

**Progress bar calculation:**
- Each card contributes to overall % based on its column position
- Planning = 0%, Coding = 33%, Testing = 66%, Done = 100%
- Total progress = average of all card positions

The main kanban board card auto-updates in real-time when sub-tasks move.

---

## Advanced Features

### Swimlanes by Assignee (V1 — Phase 3)

> **Design decision:** ON by default when multiple assignees exist on a task.

When multiple helpers are on a task, add horizontal rows per person:

```
              Planning    │  Coding      │  Testing     │  Done
─────────────────────────┼──────────────┼──────────────┼────────
👤 Alice      │ Card A    │  Card C      │              │ Card E
─────────────────────────┼──────────────┼──────────────┼────────
👤 Diana      │           │  Card B      │  Card D      │
─────────────────────────┼──────────────┼──────────────┼────────
Unassigned    │ Card F    │              │              │
```

- **ON by default** when 2+ assignees exist on cards
- Toggle off via toolbar button (single-assignee tasks never show swimlanes)
- "Unassigned" swimlane for unclaimed cards
- Helps visualize workload distribution

### WIP Limits (V2)

- Optional max-cards-per-column setting
- Column header turns red/warning when exceeded
- Encourages finishing work before starting new items
- Configurable per-task (some tasks need strict limits, others don't)

### Card Dependencies

- Visual arrows/lines between dependent cards
- "Blocked" badge on cards waiting for dependencies
- Drag-to-connect to create dependency relationships

### Quick-Add from Main Board

- Right-click task card on main board → "Open Lifecycle Board"
- Double-click task card → opens lifecycle board (instead of modal)
- Keyboard shortcut (Enter) on selected card → opens lifecycle board

---

## Data Model Impact

### Existing Model (Minimal Changes Needed!)

The `ChecklistItem` class already has everything we need:

```csharp
// Already exists in the model!
public class ChecklistItem
{
    public string Item { get; set; }           // Card title
    public string Status { get; set; }         // pending/coding/testing/done → COLUMN POSITION
    public bool Done { get; set; }             // Legacy compat
    public List<string> Notes { get; set; }    // Note history
    public string AssignedTo { get; set; }     // Card assignee
    public int CycleCount { get; set; }        // Testing bouncebacks
}
```

**Status values map directly to columns:**
- `pending` → Planning column
- `coding` → Coding column
- `testing` → Testing column
- `done` → Done column

### New Fields Needed

```csharp
// Add to ChecklistItem — V1 (Diana's recommendation)
public int SortOrder { get; set; }             // Card position within column (REQUIRED for V1)

// Add to ChecklistItem — V2
public DateTime? CreatedAt { get; set; }       // For card aging
public DateTime? MovedToColumnAt { get; set; } // For card aging per-column

// Add to KanbanTask — V1
public bool AutoStatus { get; set; }           // Enable auto-calculated status (default: true)

// Add to KanbanTask — V2
public string WipLimitsJson { get; set; }      // Per-column WIP limits JSON
```

> **Diana's note:** `SortOrder` is the only field that MUST be in V1 — without it we can't persist card ordering within columns. The aging fields can wait for V2.

### Database Migration

Minimal for V1 — only `SortOrder` added to ChecklistItem JSON (no schema migration needed since it's within the JSON blob) and `AutoStatus` boolean on the tasks table.

---

## New Files Required

| File | Purpose |
|------|---------|
| `TaskLifecycleBoard/lifecycle-board.html` | Main HTML/CSS/JS for the lifecycle board UI |
| `TaskLifecycleBoard/TaskLifecycleBoardForm.cs` | WinForms Form (window shell, TopMost, resizable) |
| `TaskLifecycleBoard/TaskLifecycleBoardControl.cs` | WebView2 control + C# ↔ JS message bridge |
| `TaskLifecycleBoard/TaskLifecycleBoardDocument.cs` | Document wrapper (follows existing pattern) |

### Reused Patterns

- **Drag-and-drop:** Copy from `tasks-panel.html` drag handlers
- **Message bridge:** Copy from `TasksPanelControl.cs` message handler pattern
- **Card rendering:** Adapt from existing task card HTML template
- **CSS theming:** Reuse existing CSS variables for light/dark mode
- **Markdown rendering:** Reuse existing markdown support

---

## Implementation Phases

### Phase 1: Core Window + Mini Kanban (MVP)
- [ ] Wire up "Edit" button on main board to open lifecycle board *(Diana: do this FIRST for end-to-end testing)*
- [ ] New WinForms Form (resizable, TopMost, WebView2)
- [ ] HTML layout with 4 lifecycle columns
- [ ] Render existing checklist items as cards in correct columns
- [ ] Drag-and-drop between columns (status update)
- [ ] Compact header with title + progress bar
- [ ] Save/load card positions via existing ChecklistJson
- [ ] Add `SortOrder` to ChecklistItem for persistent card ordering

### Phase 2: Phase-Anchored Notes + Gear Menu + Session Notes
- [ ] Collapsible phase notes at bottom of each column
- [ ] Wire Plan → Planning, Implementation → Coding, TestResults → Testing
- [ ] Done column: new "Completion Notes" field (or empty)
- [ ] Session Notes strip at bottom of board (maps to ContinuationNotes field)
- [ ] Gear button popover for metadata (priority, assignee, project, helpers, description)
- [ ] Inline editing for card titles
- [ ] Add Card button per column
- [ ] Card click → mini-modal within the board for full editing

### Phase 3: Auto-Status + Card Enhancements + Swimlanes
- [ ] Auto-calculate parent task status from children (with AutoStatus flag)
- [ ] Zero-items fallback to manual status
- [ ] Manual override detection (set AutoStatus=false)
- [ ] Re-enable toggle in gear menu
- [ ] Real-time progress bar calculation
- [ ] Swimlanes by assignee (ON by default when 2+ assignees)
- [ ] Assignee dropdown on cards
- [ ] Notes expansion on cards
- [ ] Cycle count display and auto-increment
- [ ] MessageBroker integration for live conflict resolution

### Phase 4: Advanced Features (V2)
- [ ] Card aging visual indicators
- [ ] WIP limits per column
- [ ] Card dependencies
- [ ] Window position/size persistence
- [ ] Multiple simultaneous lifecycle windows
- [ ] Keyboard shortcuts
- [ ] Virtual scrolling for 50+ item boards

---

## Resolved Decisions (2026-02-10)

| # | Question | Decision |
|---|----------|----------|
| 1 | Column customization | **Fixed 4 stages** (Planning/Coding/Testing/Done) — no custom columns |
| 2 | Backward compatibility | **Replace entirely** — no classic view fallback. Clean break. |
| 3 | Card detail expansion | **Mini-modal within the board** — not restricted by column width |
| 4 | Main board drag → auto-open | **No** — unintuitive, don't auto-open lifecycle board |
| 5 | Swimlane default | **ON by default** when multiple assignees exist |
| 6 | Conflict resolution | **YES** — use `MessageBroker.TasksUpdated` for live refresh |
| 7 | Performance / virtual scrolling | **Defer to V2** — not a concern for typical usage |

### Key Design Change: Continuation Notes

**PM feedback:** Continuation notes are session-level handoff notes ("where to pick up next"), written while work is IN PROGRESS. They don't belong anchored to Done.

**Solution:** Persistent collapsible "Session Notes" strip at the bottom of the entire board (see Component 4b above). The Done column gets "Completion/Release Notes" instead (or stays empty).

---

## Success Metrics

- **Zero tabs** — All information visible without switching tabs
- **Side-by-side notes** — Plan and Implementation viewable simultaneously
- **Resizable** — Window adapts to user's screen/preferences
- **Non-blocking** — Main board remains interactive while lifecycle board is open
- **Data model reuse** — Minimal/zero database migrations for V1
- **Clean build** — 0 errors, 0 warnings (maintaining 100% success rate)

---

## Diana's Backend Review (2026-02-10)

**Overall verdict:** Ready to build.

### Incorporated Feedback

| Topic | Diana's Input | Action Taken |
|-------|--------------|--------------|
| Data Model | `SortOrder` needed in V1 for card ordering, aging fields can wait | Moved `SortOrder` to V1 required, aging to V2 |
| Auto-Status | Need `AutoStatus` flag + manual override support | Added edge cases: zero items fallback, manual override, re-enable toggle |
| Phase-Anchored Notes | "Killer feature" — no DB changes needed, just JS routing | Confirmed: Plan/Impl/Test/Continuation already separate fields |
| Implementation Phases | Move "Wire up Edit button" to first step | Reordered Phase 1 checklist |
| Classic View | Keep 6-tab modal as fallback for V1 | **PM overruled: replace entirely, no fallback** |
| Conflict Resolution | Use MessageBroker.TasksUpdated for live refresh | **Approved by PM — added to Phase 3** |
| Performance | Virtual scrolling for 50+ items | **Deferred to V2 per PM** |

### Diana's Key Insight

> Phase-Anchored Notes requires **zero data model changes** — Plan, ImplementationSummary, TestResults, and ContinuationNotes are already separate fields on KanbanTask. The C# side just passes all 4 fields when loading, and the JS picks which column footer to render them in. Clean.
