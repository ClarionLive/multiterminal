# Kanban Task Workflow System - Design Proposal

> **Authors:** Diana (backend/architecture) + Alice (skill/UX)
> **Date:** 2026-02-09
> **Status:** Awaiting John's approval

---

## Vision

A fully automated task lifecycle that keeps agents on track without hand-holding. When an agent starts a session, it automatically knows what to work on, where to pick up, and what comes next. No skipping steps, no premature completion, no juggling multiple tasks.

---

## What Already Exists (60-70% built!)

Following our "explore before coding" principle, we found significant infrastructure already in place:

### KanbanTask Model (existing fields)
| Field | Type | Purpose |
|-------|------|---------|
| `SubStatus` | string | `"active"` or `"paused"` for in_progress tasks |
| `PausedAt` | datetime | When task was paused |
| `ChecklistJson` | JSON | `[{"item": "...", "done": true/false}]` |
| `ImplementationChecklistJson` | JSON | Separate tracking for what was built |
| `Plan` | markdown | Implementation plan |
| `TestResults` | markdown | Test outcomes |
| `ImplementationSummary` | markdown | What was actually built |
| `Helpers` | list | Assigned helper terminal names |
| `Assignee` | string | Who claimed the task |
| Stale tracking fields | various | 7/14 day flagging system |

### TaskSummary Model (existing - separate table)
| Field | Type | Purpose |
|-------|------|---------|
| `WorkCompleted` | text | Description of work done so far |
| `NextSteps` | text | What needs to be done next |
| `Blockers` | text | Any blockers |
| `Notes` | text | Additional context |
| `TriggeredBy` | string | `'status_change'`, `'checkpoint'`, `'session_end'` |

### Also Existing
- Session startup hooks that query tasks and inject context
- Agent online/offline tracking
- Backend MessageBroker operations for updating plans, checklists, summaries (but NOT exposed as MCP tools or REST endpoints)

---

## The Gaps (what we need to build)

1. **MCP Tool Gap** - Backend supports updates but no MCP tools or REST endpoints exist for: updating plans, checklists, continuation notes, summaries
2. **ContinuationNotes field** - Quick "pick up here" context on the task itself (separate from detailed TaskSummary records)
3. **"Only One Active" enforcement** - SubStatus exists but nothing enforces the single-active rule
4. **Richer checklist item states** - Currently just done/not-done boolean; need full state machine
5. **`/kanban-task` skill** - The workflow orchestrator
6. **Startup hook enhancement** - Detect active task + inject continuation context + workflow guidance

---

## Design Decisions

### 1. Enhanced Checklist Item Model

**Current format:**
```json
[{"item": "Setup database", "done": true}]
```

**Proposed format:**
```json
[
  {
    "item": "Add ContinuationNotes field to KanbanTask",
    "status": "pending",
    "notes": []
  }
]
```

**Proposed format (after work has cycled):**
```json
[
  {
    "item": "Add ContinuationNotes field to KanbanTask",
    "status": "testing",
    "notes": [
      {
        "by": "Diana",
        "at": "2026-02-09T10:00Z",
        "transition": "coding → testing",
        "text": "Added field to model + migration. Verified build clean. Field persists in DB."
      },
      {
        "by": "John",
        "at": "2026-02-09T11:00Z",
        "transition": "testing → coding",
        "text": "Field shows in UI but truncates at 500 chars. Needs to handle longer text. Also add a scrollbar."
      },
      {
        "by": "Diana",
        "at": "2026-02-09T12:00Z",
        "transition": "coding → testing",
        "text": "Changed to TEXT type (no length limit). Added scrollbar to UI panel. Verified with 2000+ char input."
      },
      {
        "by": "John",
        "at": "2026-02-09T13:00Z",
        "transition": "testing → done",
        "text": "Works perfectly. Scrollbar is smooth, long text preserved."
      }
    ]
  }
]
```

**Fields:**
- `item` - Description of the work
- `status` - Current state (see state machine below)
- `notes` - Array of transition notes, building a conversation trail

Every status transition **requires** a note. This creates an audit trail showing what was done, what needs fixing, and what was fixed - across as many cycles as needed.

### 2. State Machine (Cyclical)

Each checklist item follows the same lifecycle. The key insight: **coding and testing can cycle multiple times** before an item is done.

```
                    ┌─────────────────────────┐
                    │                         │
                    ▼                         │
pending ──→ coding ──→ testing ──→ done      │
              ▲            │                  │
              │            │                  │
              └────────────┘                  │
              (needs more work,               │
               John adds notes)               │
```

#### Transitions and Ownership

| Transition | Who | Required | Notes Content |
|------------|-----|----------|---------------|
| `pending → coding` | Agent | — | Agent starts work |
| `coding → testing` | Agent | **Notes required** | What was coded, what to test, what changed |
| `testing → coding` | John | **Notes required** | What failed, improvement suggestions, what to fix |
| `testing → done` | John | **Notes required** | Confirmation it works, any final comments |

**Rules:**
- Agent can move: `pending → coding` and `coding → testing`
- John can move: `testing → coding` and `testing → done`
- Every `coding → testing` transition MUST include notes (what was done)
- Every `testing → coding` transition MUST include notes (what needs fixing)
- Every `testing → done` transition MUST include notes (confirmation)
- No skipping states. Can't go `pending → testing` or `coding → done`.
- **Can cycle `coding ↔ testing` as many times as needed** - each round adds to the notes history

#### Why Notes Are Mandatory

The notes trail serves multiple purposes:
1. **Handoff context** - If context runs out mid-cycle, the next session reads the notes to understand the current state
2. **Accountability** - Clear record of who did what and why
3. **Learning** - Agents can see patterns in what gets sent back (common issues to avoid)
4. **Review** - John can see the full history of each item at a glance

#### Task-Level Status (unchanged)
```
todo ──→ in_progress ──→ done
              │
          SubStatus:
          active / paused
```

No new top-level statuses. The existing 4 (`todo`, `in_progress`, `done`, `suggestion`) plus `SubStatus` (`active`/`paused`) are sufficient. Granularity lives in the checklist items, not the task status.

#### Task Completion Gate

A kanban task can only be marked `done` when **ALL** checklist items have `status: "done"`. Not before. The final note on the last item is the completion confirmation.

### 3. ContinuationNotes Field

**Purpose:** Quick "pick up here" context for session handoffs.

**Separate from TaskSummary because:**
- `ContinuationNotes` = fast, on the task itself, read by startup hook
- `TaskSummary` = detailed historical record, separate table, full audit trail

**Auto-written at:**
- Every checklist item completion (checkpoint - guaranteed recovery point)
- Session end / context exhaustion
- Manual save via skill

**Contains:**
- Which file(s) were being edited (and line numbers if possible)
- Current checklist item being worked on
- What's done, what's in progress, what's next
- Any blockers or decisions pending

**Read by startup hook and injected as:**
```
ACTIVE TASK: [title]
CONTINUE FROM: [continuation notes]
CHECKLIST: 4/7 items done, currently on item 5
```

### 4. "Only One Active Task" Rule

When a task is set to `active`:
1. Query all other `in_progress/active` tasks for that assignee
2. Auto-pause them (set `SubStatus = "paused"`, `PausedAt = now`)
3. Return which tasks were paused: *"Task X set Active. Auto-paused: Task Y, Task Z"*

This prevents agents from juggling multiple tasks.

### 5. Gate Rules (enforced by skill)

| Rule | Enforcement |
|------|-------------|
| Can't move item to `testing` unless it's been in `coding` | State machine - no skipping |
| Can't move item to `done` unless it's in `testing` | Only John can approve from testing |
| Every `coding → testing` transition requires notes from agent | Mandatory notes gate |
| Every `testing → coding` transition requires notes from John | Mandatory notes gate |
| Every `testing → done` transition requires notes from John | Mandatory notes gate |
| Can't mark task `complete` if ANY checklist item isn't `done` | Completion gate |
| Can't set a new task `active` without pausing current | Auto-handled by "set active" endpoint |
| Agents can't move items from `testing` → `done` | Only John can approve |
| Agents can't move items from `testing` → `coding` | Only John sends items back |
| Item bounces coding↔testing 4+ times | Auto-escalation flag + inbox notification |
| John can add/edit checklist items anytime | PM override - not restricted to planning phase |
| Task claimer assigns helpers to specific items | Helper sees only their assigned items |

### 6. Session Startup Automation

When an agent starts a session:

```
1. Is there an Active task for me?
   YES → Inject continuation notes, show checklist progress,
         suggest next action ("Continue item 5: Add REST endpoint")
   NO  → Is there a Paused task?
         YES → Ask: "Resume paused task X, or pick a new one?"
         NO  → Show To Do column, suggest claiming next task
```

---

## The `/kanban-task` Skill

### Sub-commands

| Command | Purpose | Behavior |
|---------|---------|----------|
| `/kanban-task` | Auto-detect | Show current state, suggest next action. Default mode. |
| `/kanban-task plan` | Plan phase | Create/update the plan and checklist for current task |
| `/kanban-task progress` | Update progress | Move current checklist item forward, auto-write continuation |
| `/kanban-task continue` | Resume work | Read continuation notes, show where to pick up |
| `/kanban-task complete` | Finish task | Run ALL completion gates. Mark done only if everything passes. |

### Workflow Enforced by Skill

```
Create task → Claim → Plan (write checklist) → Work items → Complete
                                                    │
                                                    │  Per checklist item:
                                                    │  ┌──────────────────────────┐
                                                    │  │ coding (agent works)     │
                                                    │  │   ↓ (agent adds notes)   │
                                                    │  │ testing (John tests)     │
                                                    │  │   ↓ pass? → done         │
                                                    │  │   ↓ fail? → coding again │
                                                    │  └──────────────────────────┘
                                                    │
                                                    ├─ Can't skip planning
                                                    ├─ Can't skip coding→testing cycle
                                                    ├─ Every transition requires notes
                                                    └─ Can't complete with ANY item not done
```

---

## Implementation Plan

### Layer 1: Data & API (Diana)

- [ ] Add `ContinuationNotes` text field to KanbanTask model
- [ ] Database migration for new field
- [ ] Enhance `ChecklistItem` class: add `status` (string) and `notes` (array) fields
- [ ] New MCP tools:
  - `update_task_plan` - Set/update plan field
  - `update_checklist_item` - Transition a checklist item status WITH required notes
  - `update_task_continuation` - Write continuation notes
  - `update_task_summary` - Update implementation summary / test results
  - `set_task_active` - Set active with auto-pause of others
  - `get_task_detail` - Get full task with checklist + notes history
- [ ] Corresponding REST endpoints for each
- [ ] State machine validation on transitions (reject invalid moves)
- [ ] Auto-write continuation notes as side effect of checklist transitions

### Layer 2: Skill (Alice)

- [ ] `/kanban-task` skill with sub-command routing
- [ ] Auto-detect state logic (active task? paused? nothing?)
- [ ] Gate enforcement for each sub-command
- [ ] Cyclical state machine validation (coding→testing→coding→testing→done)
- [ ] Mandatory notes prompting at every transition ("What did you code? What should John test?")
- [ ] Helpful progress display ("Item 3/7: in testing - 2 cycles so far, last note from John: 'fix regex'")
- [ ] Completion gate: verify ALL items `done` before allowing task completion

### Layer 3: Hooks (Together)

- [ ] Enhance `session-status-hook.js` to:
  - Detect active task for current agent
  - Read ContinuationNotes
  - Inject rich context: task title, continuation notes, checklist progress
  - Suggest next action
- [ ] Add continuation auto-write on session-end event
- [ ] Ensure startup hook works for both fresh sessions and resumed sessions

### Layer 4: User Inbox (Phase 2 - after core workflow is stable)

- [ ] `User` model and database table
- [ ] `InboxMessage` model and `user_inbox` database table
- [ ] Auto-insert inbox messages on checklist transitions (testing, escalation, completion)
- [ ] MCP tools: `get_inbox`, `mark_inbox_read`, `reply_to_inbox`
- [ ] REST endpoints for inbox CRUD
- [ ] Inbox UI panel (unread badge, message list, quick actions)
- [ ] 4-cycle escalation detection and auto-notification

---

## Migration Strategy

The enhanced `ChecklistJson` format is backwards-compatible:
- Old format: `{"item": "...", "done": true}`
- New format: `{"item": "...", "status": "done", "notes": [...]}`
- Migration: Treat missing `status` as `"done"` if `done=true`, else `"pending"`. Treat missing `notes` as `[]` (empty history).
- Existing checklists continue to work; new fields are additive.

---

## John's Decisions

1. **Checklist granularity** → Agent's judgment. No minimum enforced.
2. **Notification preferences** → Yes, notify - via a new **User Inbox system** (see below).
3. **Multi-agent on same task** → Task claimer assigns helpers to specific checklist items.
4. **Priority of implementation** → Layer 1 first (data & API), so the skill has APIs to call.
5. **Who creates checklist items?** → Both. Agents create during planning, John can add/edit anytime as PM.
6. **Max cycles before escalation?** → 4 cycles. After 4 coding↔testing bounces, flag for discussion.

---

## New Feature: User Inbox System

> Added based on John's feedback on notification preferences.

### Concept

A notification inbox for **project managers / human users** (not agents). When something needs John's attention, a short note lands in his Inbox - similar to receiving email.

### User Model

| Field | Type | Purpose |
|-------|------|---------|
| `Id` | string | Unique user identifier |
| `Name` | string | Display name (e.g., "John") |
| `Role` | string | `"project_manager"`, `"developer"`, etc. |
| `ProjectIds` | list | Projects this user is assigned to |

### Inbox Message Model

| Field | Type | Purpose |
|-------|------|---------|
| `Id` | string | Unique message identifier |
| `UserId` | string | Recipient user |
| `TaskId` | string | Related kanban task |
| `ChecklistItemIndex` | int? | Related checklist item (if applicable) |
| `Type` | string | `"ready_for_testing"`, `"escalation"`, `"task_complete"`, `"helper_request"` |
| `Summary` | text | Short note: what was done, what needs review |
| `CreatedAt` | datetime | When the notification was created |
| `CreatedBy` | string | Who triggered it (agent name) |
| `ReadAt` | datetime? | When John read it (null = unread) |

### Auto-Generated Inbox Messages

| Trigger | Type | Summary Example |
|---------|------|-----------------|
| Checklist item moves to `testing` | `ready_for_testing` | "Diana finished 'Add validation to login form' - builds clean, ready for your testing" |
| Item bounces 4+ times | `escalation` | "Item 'Fix regex validation' has cycled 4 times - may need discussion" |
| All checklist items done | `task_complete` | "All 7 items on 'Login Feature' are done - task ready for final sign-off" |
| Helper requests guidance | `helper_request` | "Alice needs clarification on 'Add dark mode' item 3" |

### Inbox UI

A simple reviewable list (newest first) showing:
- Unread count badge
- Task title + item name
- Short summary
- Quick actions: Mark read, Jump to task, Reply with notes

### Implementation Notes

- New database table: `user_inbox`
- New MCP tools: `get_inbox`, `mark_inbox_read`, `reply_to_inbox`
- New REST endpoints for inbox CRUD
- Auto-insert triggered by checklist state transitions (side effect of `update_checklist_item`)
- Inbox messages are lightweight - just pointers to the real data on the task

> **Scope note:** The Inbox system is a natural extension but could be built as a **separate phase** after the core workflow is solid. Recommended: build core workflow first (Layers 1-3), then add Inbox as Layer 4.
