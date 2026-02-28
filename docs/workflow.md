# MultiTerminal Workflow Guide

This document defines the standardized workflow, terminology, and task stages for MultiTerminal collaboration.

## Terminology

| Term | Definition | Example |
|------|------------|---------|
| **Task** | A goal or deliverable tracked on the Kanban board | "Add drag-and-drop reordering" |
| **Plan** | Execution strategy for complex tasks with phases, assignments, and decisions | "Task-Centric Memory System" |
| **Phase** | A stage of work within a Plan | design, coding, testing, completed |
| **Checklist Item** | A specific step within a phase | "Create DragHandler.cs" |
| **Assignment** | A terminal's piece of work within a Plan | "Alice: UI components" |
| **Decision** | A recorded choice with rationale | "Use SQLite for persistence" |
| **Pool** | Shared memory/learnings across terminals | LEARNED messages, cross-terminal context |

> **Task vs Assignment**: A Task lives on the Kanban board as a goal to accomplish. An Assignment is a terminal's specific work unit within a Plan. One Task may spawn multiple Assignments across team members.

## Task Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                         KANBAN BOARD                            │
├────────────┬────────────┬────────────┬────────────┬────────────┤
│ Suggestion │    Todo    │ In Progress│    Done    │            │
├────────────┼────────────┼────────────┼────────────┼────────────┤
│  Ideas &   │  Approved  │  Active    │ Completed  │            │
│  proposals │  work      │  work      │ & verified │            │
└────────────┴─────┬──────┴──────┬─────┴────────────┴────────────┘
                   │             │
                   ▼             │
            [Move to In Progress]│
                   │             │
                   ▼             ▼
            ┌────────────────────────┐
            │  Simple or Complex?    │
            └───────┬────────┬───────┘
                    │        │
              Simple│        │Complex
                    ▼        ▼
              Just do it   Create PLAN
                    │        │
                    │        ▼
                    │   ┌─────────────────────────────────────┐
                    │   │              PLAN                   │
                    │   ├─────────────────────────────────────┤
                    │   │  Phase: design                      │
                    │   │    [ ] Define requirements          │
                    │   │    [ ] Identify affected files      │
                    │   │    [ ] Team alignment               │
                    │   │                                     │
                    │   │  Phase: coding                      │
                    │   │    [ ] Implement feature            │
                    │   │    [ ] Write unit tests             │
                    │   │                                     │
                    │   │  Phase: testing                     │
                    │   │    [ ] Integration testing          │
                    │   │    [ ] Edge case validation         │
                    │   │                                     │
                    │   │  Phase: completed                   │
                    │   │    [ ] Leader sign-off              │
                    │   │    [ ] Documentation                │
                    │   └──────────────────┬──────────────────┘
                    │                      │
                    │    ┌─────────────────┤
                    │    │                 │
                    │    ▼                 ▼
                    │  [Blocked?]     Plan Complete
                    │    │                 │
                    │    ▼                 │
                    │  Resolve blocker     │
                    │  (stays In Progress) │
                    │    │                 │
                    ▼    ▼                 ▼
                    └────┴────► Task → Done
```

### Handling Complexity Emergence

If a "simple" task grows complex mid-implementation:
1. **Pause** - Stop the simple path
2. **Create a Plan** - Don't force complex work through the simple path
3. **Continue** - Work through phases as normal

### Plan Lifecycle Notes

- **Pause**: Plans can be paused if priorities shift (set status to 'paused')
- **Abandon**: If a Plan is no longer relevant, mark as 'abandoned' with rationale
- **Resume**: Paused plans can be reactivated with `set_active_plan`

## When to Create a Plan

| Scenario | Plan Needed? |
|----------|--------------|
| Single-file bug fix | No |
| Quick UI tweak | No |
| Multi-file feature | Yes |
| Multiple team members involved | Yes |
| Architectural decisions required | Yes |
| Estimated effort > 1 hour | Yes |
| Uncertainty about approach | Yes |

**Rule of thumb**: If you need to coordinate with others or make decisions that affect the codebase structure, create a Plan.

## Task Statuses

### Kanban Task Statuses

| Status | Meaning | Who moves it |
|--------|---------|--------------|
| `suggestion` | Idea proposed, not yet approved | Anyone can propose |
| `todo` | Approved, ready to be worked on | Leader or team consensus |
| `in_progress` | Actively being worked on | Assignee claims it |
| `done` | Completed and verified | Leader or assignee |

### Plan Phases

| Phase | Purpose | Typical Checklist Items |
|-------|---------|------------------------|
| `design` | Requirements, approach, team alignment | Define scope, identify files, get approval |
| `coding` | Implementation | Write code, create tests, integrate |
| `testing` | Validation | Unit tests, integration tests, edge cases |
| `completed` | Wrap-up | Leader sign-off, documentation |

### Assignment Statuses

| Status | Meaning |
|--------|---------|
| `assigned` | Work allocated but not started |
| `in_progress` | Actively working |
| `blocked` | Waiting on something (specify what) |
| `done` | Assignment completed |

## Live Activity Tracking

Each terminal reports their current activity status for real-time visibility.

### Database Schema

```sql
CREATE TABLE terminal_activity (
    terminal TEXT PRIMARY KEY,
    status TEXT,           -- 'working', 'idle', 'blocked', 'unknown'
    activity TEXT,         -- current work description
    task_id TEXT,          -- related Kanban task
    plan_id TEXT,          -- related Plan (if any)
    updated_at DATETIME
);
```

### Fields

| Field | Description | Example |
|-------|-------------|---------|
| `terminal` | Who | "Alice" |
| `status` | Current state | "working", "idle", "blocked", "unknown" |
| `activity` | What they're doing | "Implementing DragHandler.cs" |
| `task_id` | Related Kanban task | "15dc7cbd" |
| `plan_id` | Related Plan (if any) | "plan0001" |
| `updated_at` | Last update time | "2026-02-03 10:15:00" |

### Staleness Handling

- If no update received in 5 minutes, status auto-changes to `unknown`
- Terminals should heartbeat on state changes or every few minutes
- `unknown` status signals the terminal may be disconnected or crashed

### Startup Context Integration

Team status is included in startup context:
```
## Team Status
- Alice: working - "Implementing DragHandler.cs"
- Bob: idle
- Charlie: blocked - "Waiting on API spec"
- Diana: unknown (last seen 10 min ago)
```

### Auto-Update Integration

Activity auto-updates when:
- `update_assignment_status` is called (assignment → activity)
- `claim_task` is called (task claimed → working on task)
- `update_task_status` to done (→ idle)

### Benefits

- Real-time visibility into team progress
- Coordination to avoid conflicts
- Quick identification of blocked work
- User can see what each Claude is doing

## Workflow Rules

1. **One active Plan at a time** - Team focuses on a single mission
2. **Leader coordinates** - Plan leader assigns work and makes decisions
3. **Record decisions** - Use `record_decision` for choices with rationale
4. **Update status promptly** - Keep assignment and activity status current
5. **Report blockers immediately** - Don't sit on blocked status silently
6. **Test before done** - Tasks aren't done until verified working
7. **Escalate complexity** - If simple becomes complex, create a Plan

## Quick Reference: MCP Tools

### Task Management
- `list_tasks` - View Kanban board
- `create_task` - Add new task
- `claim_task` - Take ownership of a task
- `update_task_status` - Move task between columns

### Plan Management
- `create_plan` - Start a new plan
- `get_active_plan` - View current plan details
- `set_active_plan` - Activate an existing plan
- `update_phase` - Advance to next phase
- `add_checklist_item` - Add step to a phase
- `update_checklist_item` - Mark step done/undone

### Assignments
- `assign_terminal` - Give someone a role in the plan
- `get_my_assignment` - Check your current assignment
- `update_assignment_status` - Report progress (in_progress, blocked, done)

### Activity Tracking (Proposed)
- `update_activity` - Report current work status
- `get_team_activity` - View all terminal statuses

### Coordination
- `send_message` - Direct message to a terminal
- `broadcast` - Message all terminals
- `record_decision` - Log a decision with rationale
