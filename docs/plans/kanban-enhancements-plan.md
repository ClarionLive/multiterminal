# Kanban Board Enhancements - Implementation Plan

**Created**: 2026-02-03
**Lead**: Charlie
**Contributors**: Alice, Charlie
**Status**: Approved for Implementation

## Overview

Three features to enhance the kanban board: column visibility controls, card CRUD operations, and drag-and-drop functionality.

**Implementation Order**: Phase 1 → 2 → 3 (simplest first, each builds on previous)

---

## Phase 1: Hide/Unhide Columns

### UI Design
- **Toggle**: Eye icon button in each column header
- **Hidden indicator**: Pill/badge in toolbar area (e.g., "2 columns hidden")
- **Restore**: Click pill to show dropdown menu of hidden columns to restore

### Technical Approach

| Layer | Changes |
|-------|---------|
| Frontend | Add eye icon buttons, CSS for hidden state, toolbar badge component |
| Storage | localStorage key `kanban-column-visibility` (JSON object of column→boolean) |
| Backend | None required (client-side preference only) |

### Behavior
- Hidden columns get `display: none`
- Cards in hidden columns remain in data, just not visible
- Visibility persists across page reloads via localStorage

---

## Phase 2: Add/Edit/Delete Cards

### UI Design

| Action | UI Pattern | Rationale |
|--------|------------|-----------|
| **Add** | Modal dialog with title + description fields | Clean slate, proper form |
| **Edit (quick)** | Click title to edit inline | Fast for small tweaks |
| **Edit (full)** | Icon opens modal with all fields + status dropdown | Comprehensive changes |
| **Delete** | Trash icon on hover → confirmation popover | Lightweight, non-disruptive |

### Technical Approach

| Layer | Changes |
|-------|---------|
| Frontend | Add modal component, inline edit handlers, popover component, hover icons |
| WebView Bridge | New message types: `edit_task`, `delete_task` |
| MessageBroker | Add `UpdateTask(taskId, title, description)` method |
| Database | Already has `DeleteTask()`, add update method |
| MCP Tools | Add `edit_task` tool for Claude agents |

### Behavior
- Add button: "+" in column header or floating action button
- Edit icon: Pencil appears on card hover
- Delete icon: Trash appears on card hover
- Popover anchors to delete button, closes on Cancel or click-outside

---

## Phase 3: Drag & Drop Cards

### UI Design
- **Drag handle**: Entire card is draggable
- **Visual feedback**: Ghost image during drag, drop zone highlights on valid targets
- **Drop zones**: Each column's `.column-tasks` container

### Technical Approach

| Layer | Changes |
|-------|---------|
| Frontend | HTML5 drag events: `dragstart`, `dragend`, `dragover`, `dragenter`, `dragleave`, `drop` |
| WebView Bridge | Reuse existing `update_task` message (status change) |
| Backend | Reuse existing `UpdateTaskStatus()` method |
| Database | No schema changes |

### Behavior
- Drag card → drop on different column → status updates to match column
- Cards sorted by `created_at` within columns (no manual reordering in v1)
- CSS transitions for smooth drop feedback
- Invalid drops (e.g., same column) have no effect

### Why Native HTML5 (Not SortableJS)
- Desktop-only WebView2 app, no touch support needed
- Fewer dependencies, less maintenance
- Modern browsers handle basics well
- Can add CSS transitions for visual polish

---

## Deferred (YAGNI)

- **Card ordering within columns**: Adds complexity with `order` field, extra DB writes. Add later if users request it.
- **Column reordering**: Fixed 4-column structure is sufficient for now.
- **Multi-select drag**: Single card drag covers primary use case.

---

## Files to Modify

| File | Phase | Changes |
|------|-------|---------|
| `TasksPanel/tasks-panel.html` | 1, 2, 3 | UI components, event handlers, CSS |
| `TasksPanel/TasksPanelControl.cs` | 2 | Handle new message types |
| `MCPServer/Services/MessageBroker.cs` | 2 | Add `UpdateTask()` method |
| `Services/TaskDatabase.cs` | 2 | Add update query |
| `MCPServer/Tools/TaskTools.cs` | 2 | Add `edit_task` MCP tool |

---

## Summary

| Phase | Feature | Complexity | Backend Changes |
|-------|---------|------------|-----------------|
| 1 | Hide/Unhide Columns | Low | None |
| 2 | Add/Edit/Delete Cards | Medium | UpdateTask method, new message types |
| 3 | Drag & Drop | Medium | None (reuses existing) |

---

## Collaboration Notes

- Alice suggested implementation order (simplest → complex)
- Alice recommended native HTML5 over SortableJS for desktop-only app
- Hybrid edit pattern (inline + modal) was Alice's idea
- YAGNI on card ordering agreed by both
