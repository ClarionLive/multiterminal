# MultiTerminal

A Windows application for managing multiple Claude Code terminal sessions with inter-terminal messaging, coordinated task management, and a plan-centric workflow system.

## Features

- **Multiple Terminal Sessions**: Run multiple Claude Code instances in a tabbed interface
- **Inter-Terminal Messaging**: Claude instances can communicate with each other via MCP tools
- **Plan System**: Coordinate work across terminals with phases, assignments, and checklists
- **Session Persistence**: Resume sessions and maintain identity across restarts
- **Startup Context Injection**: Automatic plan context provided to terminals at session start

## Plan System Overview

The Plan System enables coordinated multi-agent workflows where multiple Claude terminals collaborate on a shared goal. Plans progress through defined phases with team assignments and decision tracking.

### Core Concepts

**Plans** track goals from inception to completion:
- **Title & Description**: What the plan aims to accomplish
- **Content**: Detailed implementation details
- **Leader**: Terminal responsible for coordination
- **Status**: `draft`, `active`, `paused`, `completed`, `abandoned`

**Phases** represent workflow stages:
- `design` - Planning and architecture
- `coding` - Implementation
- `testing` - Validation and QA
- `completed` - Done

Each phase can have a **checklist** of items to track progress.

**Assignments** link terminals to plans:
- **Role**: `leader` or `member`
- **Task Summary**: What the terminal is working on
- **Status**: `assigned`, `in_progress`, `blocked`, `done`

**Decisions** record important choices made during execution for future context.

### Workflow Example

1. Leader creates a plan with `create_plan`
2. Terminals are assigned via `assign_terminal`
3. Work begins, assignments move to `in_progress`
4. Leader advances phases with `update_phase`
5. Decisions are recorded with `record_decision`
6. Checklist items track phase completion
7. Plan completes when all phases are done

### Startup Context

When a terminal starts, it automatically receives:
- Active plan title, phase, and their assignment
- Phase checklist items
- Recent pool activity (shared learnings)

This enables terminals to resume work with full context.

---

## MCP Tools Reference

The MultiTerminal MCP server exposes tools in three categories: **Messaging**, **Plan Management**, and **Task Management**.

### Messaging Tools

#### `register_terminal`
Register this terminal for messaging.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Friendly name (e.g., 'Alice', 'Bob') |
| `doc_id` | string | Yes | Value from `$env:MULTITERMINAL_DOC_ID` |

#### `list_terminals`
List all registered terminals available for messaging.

*No parameters*

Returns: Array of `{id, name, docId}`

#### `send_message`
Send a message to another terminal.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `from_terminal_id` | string | Yes | Your terminal ID (from registration) |
| `to` | string | Yes | Target terminal name or ID |
| `message` | string | Yes | Message content |

#### `get_messages`
Get pending messages sent to your terminal. Messages are removed after retrieval.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `terminal_id` | string | Yes | Your terminal ID |

#### `broadcast`
Send a message to all other registered terminals.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `from_terminal_id` | string | Yes | Your terminal ID |
| `message` | string | Yes | Message to broadcast |

#### `get_resume_session`
Get the Claude Code session ID for resuming a terminal identity.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Identity name |
| `project_path` | string | No | Project path (defaults to cwd) |

#### `list_identities`
List all known terminal identities for a project.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `project_path` | string | No | Project path (defaults to cwd) |

---

### Plan Management Tools

#### `create_plan`
Create a new plan with phases.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `title` | string | Yes | Short plan title |
| `description` | string | No | Detailed goal description |
| `content` | string | No | Full implementation details |
| `leader_id` | string | No | Terminal name of leader |
| `set_active` | bool | No | Set as active immediately (default: true) |

#### `get_active_plan`
Get the currently active plan with phases and assignments.

*No parameters*

Returns: Plan info, phases with checklists, assignments, recent decisions

#### `set_active_plan`
Set an existing plan as the active plan.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `plan_id` | string | Yes | Plan ID to activate |

#### `list_plans`
List all plans with their status.

*No parameters*

Returns: Array of plans with active/draft/completed counts

#### `update_phase`
Advance or change the current phase of a plan.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `plan_id` | string | Yes | Plan ID |
| `new_phase` | string | Yes | Phase: `design`, `coding`, `testing`, `completed` |

#### `add_checklist_item`
Add a new checklist item to a phase.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `plan_id` | string | Yes | Plan ID |
| `phase` | string | Yes | Phase name |
| `item_text` | string | Yes | Checklist item text |

#### `update_checklist_item`
Update a checklist item (mark done/undone).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `plan_id` | string | Yes | Plan ID |
| `phase` | string | Yes | Phase name |
| `item_index` | int | Yes | Item index (0-based) |
| `done` | bool | Yes | Whether item is done |

---

### Assignment Tools

#### `assign_terminal`
Assign a terminal to a role/task within a plan.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `plan_id` | string | Yes | Plan ID |
| `terminal_name` | string | Yes | Terminal name to assign |
| `role` | string | No | `leader` or `member` (default: member) |
| `task_summary` | string | No | Summary of assigned task |

#### `get_my_assignment`
Get your assignment in the active plan.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `terminal_name` | string | Yes | Your terminal name |

Returns: Plan info, current phase, your assignment details

#### `update_assignment_status`
Update the status of an assignment.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `plan_id` | string | Yes | Plan ID |
| `terminal_name` | string | Yes | Terminal whose assignment to update |
| `status` | string | Yes | `assigned`, `in_progress`, `blocked`, `done` |
| `blocked_by` | string | No | What's blocking (if status is `blocked`) |

---

### Decision Recording

#### `record_decision`
Record a decision made during plan execution for future context.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `plan_id` | string | Yes | Plan ID |
| `phase` | string | Yes | Phase when decision was made |
| `decision_text` | string | Yes | The decision |
| `rationale` | string | No | Why this decision was made |
| `decided_by` | string | No | Terminal name of decision maker |

---

## Architecture

```
MultiTerminal/
├── MCPServer/
│   ├── MultiTerminalMcpServer.cs   # HTTP MCP server host
│   ├── Models/
│   │   ├── Plan.cs                 # Plan, PlanPhase, PlanAssignment, PlanDecision
│   │   ├── Message.cs              # Inter-terminal messages
│   │   └── TerminalInfo.cs         # Terminal registration
│   ├── Services/
│   │   ├── MessageBroker.cs        # Message routing
│   │   ├── PoolCoordinator.cs      # Shared memory/learnings
│   │   └── SessionDiscovery.cs     # Session ID discovery
│   └── Tools/
│       ├── MessagingTools.cs       # Messaging MCP tools
│       ├── PlanTools.cs            # Plan management MCP tools
│       └── TaskTools.cs            # Kanban task tools
├── Services/
│   ├── SessionDatabase.cs          # SQLite for sessions/identities
│   └── PlanDatabase.cs             # SQLite for plans/phases/assignments
└── Terminal/
    └── TerminalDocument.cs         # Terminal UI hosting
```

## Configuration

The MCP server runs on `http://localhost:5050/mcp` by default.

Configure Claude Code to use the MultiTerminal MCP server in your `~/.claude/settings.json` or project's `.claude/settings.json`:

```json
{
  "mcpServers": {
    "multiterminal": {
      "url": "http://localhost:5050/mcp"
    }
  }
}
```

## License

See LICENSE file for details.
