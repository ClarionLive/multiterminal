# Task Management V2 - Design Document

## Overview

A structured task management system that ensures every terminal always knows exactly what it should be working on, with built-in support for interruptions, context preservation, and team collaboration.

## Core Problem

Current system allows multiple "in_progress" tasks per terminal, making it impossible to know what was actually being worked on. No context is preserved between sessions.

## Key Principles

1. **Single Active Focus** - One terminal can only have ONE active task at a time
2. **Interruption Stack** - Urgent work can interrupt current work; paused tasks resume automatically
3. **Context Preservation** - Every status change captures progress summary for seamless handoff/restart
4. **Project Hierarchy** - Tasks live within a clear organizational structure

---

## Status Flow

```
Suggestion → To Do → In Progress → Done
                         │
                    ┌────┴────┐
                  Active    Paused
                  (1 max)   (stack)
```

### Status Definitions

| Status | Meaning |
|--------|---------|
| **Suggestion** | Idea worth considering, not committed |
| **To Do** | Committed work, waiting to be picked up |
| **In Progress: Active** | Currently being worked on RIGHT NOW (max 1 per terminal) |
| **In Progress: Paused** | Was being worked on, interrupted by higher priority |
| **Done** | Completed |

### Sub-status for In Progress

Tasks in the "In Progress" column have a sub-status:
- `active` - The ONE task currently being worked on
- `paused` - Interrupted, waiting to resume

---

## Task Stack Behavior

Each terminal maintains a **LIFO stack** of paused tasks.

### Interrupt Flow (New urgent task arrives)

```
Before:
  Active: [Task A - fixing login bug]
  Paused: []

User assigns urgent Task B to terminal...

After:
  Active: [Task B - critical security patch]
  Paused: [Task A - fixing login bug (paused_at: timestamp)]
```

### Completion Flow (Active task finished)

```
Before:
  Active: [Task B - critical security patch]
  Paused: [Task A, Task C, Task D]  (most recent first)

Terminal completes Task B...

After:
  Active: [Task A - fixing login bug]  (auto-resumed)
  Paused: [Task C, Task D]
  Done: [Task B]
```

### Empty Stack Flow (Nothing paused)

When Active task completes and paused stack is empty:
1. Terminal can wait for assignment
2. OR pick a "To Do" task that interests them

---

## Context Preservation

**CRITICAL**: Every status change MUST include a progress summary.

### Progress Summary Fields

```
{
  "summary_at": "2024-02-04T10:30:00Z",
  "triggered_by": "status_change",
  "previous_status": "active",
  "new_status": "paused",
  "work_completed": "Implemented the dropdown UI, wired up terminal list API",
  "next_steps": "Need to add toast notification on successful assignment",
  "blockers": null,
  "notes": "Dropdown positioning needs CSS tweaks on small screens"
}
```

### When Summaries Are Required

| Event | Required? | Purpose |
|-------|-----------|---------|
| Active → Paused | YES | Capture state before interruption |
| Active → Done | YES | Document completion for reference |
| Paused → Active | YES | Acknowledge resume, update on current state |
| Claim from To Do | YES | Initial plan/approach |
| Session End | YES | Auto-triggered, capture stopping point |

### Summary Storage

Summaries are stored as a log on the task:
```json
{
  "task_id": "abc123",
  "summaries": [
    { "summary_at": "...", "work_completed": "...", ... },
    { "summary_at": "...", "work_completed": "...", ... }
  ]
}
```

---

## Team Collaboration

### Roles on a Task

| Role | Count | Meaning |
|------|-------|---------|
| **Assignee** | 1 | Primary owner, responsible for completion |
| **Helpers** | 0-N | Available to assist when needed |

### Helper Behavior

- Helpers keep their own task stack
- When assignee needs help, they message the helper
- Helper can pause their current work to assist
- Helper's contribution noted in task summaries

### Helper Assignment

```
Task: "Implement OAuth integration"
Assignee: Alice
Helpers: [Bob, Charlie]

Alice is primary. Bob and Charlie will help when asked.
```

---

## Project Hierarchy

```
Project
├── name
├── description
├── kanban_board
│   ├── columns: [Suggestion, To Do, In Progress, Done]
│   └── tasks[]
│       ├── id
│       ├── title
│       ├── description
│       ├── column (status)
│       ├── sub_status (active/paused/null)
│       ├── assignee
│       ├── helpers[]
│       ├── paused_at (timestamp, if paused)
│       ├── summaries[] (progress log)
│       ├── plan (optional, for complex tasks)
│       │   ├── title
│       │   ├── phases[]
│       │   │   ├── name
│       │   │   ├── checklist[]
│       │   │   │   ├── item
│       │   │   │   └── done
│       │   │   └── status
│       │   └── current_phase
│       ├── created_at
│       ├── created_by
│       └── updated_at
└── team_members[]
```

---

## Startup Context

When a terminal starts, the hook displays:

```
## Your Current Task (Bob)
🔨 ACTIVE: [abc123] Fix login validation
   Last update: 10 min ago
   Progress: Implemented server-side validation, need client-side
   Next: Add error message display in LoginForm.tsx

📋 PAUSED (2):
   [def456] OAuth integration (paused 2 hours ago)
   [ghi789] Refactor auth module (paused yesterday)

Use update_task_status when done. Paused tasks auto-resume.
```

---

## MCP Tool Changes

### Modified Tools

**claim_task(task_id, assignee)**
- If assignee has an active task → pause it with summary prompt
- New task becomes active
- Timestamp recorded

**update_task_status(task_id, status)**
- If marking done → prompt for completion summary
- If active task done → auto-resume most recent paused
- If marking paused manually → prompt for pause summary

### New Tools

**add_task_summary(task_id, work_completed, next_steps, blockers?, notes?)**
- Add progress summary without changing status
- For mid-work checkpoints

**get_my_task_stack(terminal_name)**
- Returns active task + paused stack
- Used by startup hook

**add_helper(task_id, helper_name)**
- Add a helper to a task

**remove_helper(task_id, helper_name)**
- Remove a helper from a task

---

## Database Schema Changes

### tasks table (modified)

```sql
ALTER TABLE tasks ADD COLUMN sub_status TEXT;  -- 'active', 'paused', NULL
ALTER TABLE tasks ADD COLUMN paused_at DATETIME;
ALTER TABLE tasks ADD COLUMN project_id TEXT;
```

### task_summaries table (new)

```sql
CREATE TABLE task_summaries (
    id INTEGER PRIMARY KEY,
    task_id TEXT NOT NULL,
    summary_at DATETIME NOT NULL,
    triggered_by TEXT NOT NULL,  -- 'status_change', 'checkpoint', 'session_end'
    previous_status TEXT,
    new_status TEXT,
    work_completed TEXT,
    next_steps TEXT,
    blockers TEXT,
    notes TEXT,
    author TEXT,  -- terminal name
    FOREIGN KEY (task_id) REFERENCES tasks(id)
);
```

### task_helpers table (new)

```sql
CREATE TABLE task_helpers (
    task_id TEXT NOT NULL,
    helper_name TEXT NOT NULL,
    added_at DATETIME NOT NULL,
    PRIMARY KEY (task_id, helper_name),
    FOREIGN KEY (task_id) REFERENCES tasks(id)
);
```

### projects table (new)

```sql
CREATE TABLE projects (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL
);
```

---

## Design Decisions (Team Consensus)

*Resolved 2026-02-04 by Alice, Bob, Charlie, Diana*

### Q1: Summary Enforcement → Auto-generate with Override

**Decision**: Auto-generate summaries from context, allow manual override/enhancement.

**Implementation details**:
- Auto-generate baseline from: files edited, tools used, time spent, recent conversation
- Mark auto-generated summaries with `[Auto]` prefix
- Show draft briefly with optional 10s enhancement prompt: "Add context?"
- Fast mode (tool-based) vs Thorough mode (full context analysis)
- Fallback for empty tasks: "No progress recorded"
- Filter noise: weight successful outcomes over debugging/reverted changes

**Rationale**: Blocking creates friction at the worst time (urgent interruptions). Warning leads to skipped summaries. Auto-generation guarantees something is always captured while allowing quality enhancement when time permits.

*"Auto-gen is a floor, not a ceiling."* - Alice

---

### Q2: Auto-pause on Assign → Auto-pause with Safeguards

**Decision**: Auto-pause current work when urgent task assigned, with grace period for atomic operations.

**Implementation details**:
- **Priority levels**:
  - `urgent` - Grace period (5-30s) then auto-pause
  - `normal` - Queue behind current task
  - `low` - Add to backlog
- **Grace period**: Let atomic operations complete (mid-commit, mid-build, mid-write)
- **Critical section signaling**: Terminal can flag "don't interrupt" with bounded timeout (30s max)
- **Undo window**: Quick reversal for accidental assignments
- **Task affinity**: Surface related paused work when assigning ("Diana has related work on auth module")

**Rationale**: User assignment IS the priority decision - confirmation dialogs add friction that undermines urgency. Auto-summary (Q1) makes interruption safe by capturing context.

*"The paused stack + auto-summary already handles interruption gracefully."* - Alice

---

### Q3: Helper Notifications → Notify Immediately (Lightweight)

**Decision**: Notify helpers when added, but keep it non-blocking and informational.

**Implementation details**:
- **Two-tier notifications**:
  - "Added as helper" - Lightweight FYI: "Added as helper: Task X (Bob's task)"
  - "Help requested" - Action trigger: "Alice needs help on Task X NOW"
- **Non-blocking**: Appears in status/queue, doesn't interrupt current work
- **No action required**: Just awareness; assignee drives engagement

**Rationale**: Silent add feels like being "voluntold" - helper gets blindsided when asked to help with zero context. Notification allows mental preparation and optional context pre-loading.

*"CC on an email - you're in the loop, not on the hook yet."* - Alice

---

### Q4: When to Plan → Complexity-triggered Suggestions, Always Optional

**Decision**: Detect complexity signals and suggest planning, but never require it.

**Implementation details**:
- **Complexity signals**:
  - Multi-file scope
  - Keywords: "implement", "integrate", "migrate", "refactor"
  - Unknowns: "figure out", "investigate", "design"
  - Dependencies: mentions other systems, APIs, "after X is done"
  - Has subtasks or "and" in description
- **Prompt on claim**: "This looks complex - want to create a plan?"
- **Always skippable**: Assignee has final say
- **Emergent planning**: Re-prompt if task grows mid-work (scope creep, unexpected issues)
- **Learnable heuristic**: Track accept/skip patterns and outcomes to improve suggestions

**Rationale**: Time estimates are unreliable; complexity signals are more accurate. Pure optional leads to under-planning; blocking creates overhead resistance. Smart nudges earn trust by making good suggestions.

*"Like IDE suggestions - helpful prompts based on context, but human has final say."* - Alice

---

### Q5: Paused Task Timeout → Flag for Review

**Decision**: Yes, flag paused tasks for review after timeout.

**Implementation details**:
- Day 7: Flag task, notify assignee "Still relevant?"
- Day 14: Prompt to close or re-prioritize
- Never auto-delete - always human decision

**Rationale**: Stale paused tasks are decision debt. Escalating nudges surface them without forcing premature closure.

---

### Q6: Cross-project Tasks → Single Project Ownership

**Decision**: No, tasks belong to exactly one project.

**Implementation details**:
- Each task has single `project_id` (clear ownership)
- Cross-project *references* allowed for linking dependencies
- Avoids sync complexity while acknowledging real-world relationships

**Rationale**: Single ownership keeps accountability clear. References provide flexibility without the complexity of multi-project sync.

---

## Implementation Phases

### Phase 1: Core Stack Behavior
- [ ] Add sub_status column
- [ ] Modify claim_task for stack behavior
- [ ] Modify update_task_status for auto-resume
- [ ] Update startup hook to show stack

### Phase 2: Context Preservation
- [ ] Create task_summaries table
- [ ] Add add_task_summary tool
- [ ] Prompt for summaries on status changes
- [ ] Show summaries in startup context

### Phase 3: Team Collaboration
- [ ] Create task_helpers table
- [ ] Add helper management tools
- [ ] Show helpers on task cards

### Phase 4: Project Hierarchy
- [ ] Create projects table
- [ ] Link tasks to projects
- [ ] Project-scoped kanban views

---

## Example Scenarios

### Scenario 1: Urgent Bug Interrupts Feature Work

```
10:00 - Bob is working on "Add OAuth" (active)
10:15 - User assigns "Fix critical XSS bug" to Bob
        → "Add OAuth" becomes paused, summary captured
        → "Fix XSS bug" becomes active
10:45 - Bob completes XSS fix
        → "Fix XSS bug" moves to Done, summary captured
        → "Add OAuth" auto-resumes as active
        → Bob sees summary of where he left off
```

### Scenario 2: Multiple Interruptions

```
Bob's stack after busy morning:
  Active: [Task D - urgent prod fix]
  Paused: [Task C, Task B, Task A]  (LIFO order)

Bob completes Task D:
  Active: [Task C]  (auto-resumed)
  Paused: [Task B, Task A]
  Done: [Task D]
```

### Scenario 3: Session Restart

```
Bob's session crashes while working on Task X.

New session starts, hook shows:
  "🔨 ACTIVE: Task X - Implement caching
   Last update: 5 min ago (session ended unexpectedly)
   Progress: Added Redis client, configured connection pool
   Next: Implement cache invalidation logic"

Bob knows exactly where to pick up.
```

---

## Revision History

| Date | Author | Changes |
|------|--------|---------|
| 2026-02-04 | Bob | Initial draft based on user requirements |
| 2026-02-04 | Team (Alice, Bob, Charlie, Diana) | Resolved Q1-Q4 through team discussion. Added Design Decisions section with consensus rationale. |
