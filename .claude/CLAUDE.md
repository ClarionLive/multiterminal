# MultiTerminal Project

A multi-agent coordination system for Claude Code. WinForms desktop app (C#/.NET) with integrated REST API (port 5050), MCP server, and WebView2-based UI panels.

---

## Message Delivery Nudge (CRITICAL)

When you see `[cm]` as user input, it means you have a new message. **You MUST immediately check your messages:**

1. Call `get_messages(terminalId="your-terminal-id")` to read new messages
2. Process the message and respond appropriately
3. If it's from another terminal/agent, reply using `send_message`

`[cm]` is injected automatically by the InboxMonitorService when a message arrives for your terminal. Do NOT ignore it, do NOT ask the user about it — just check your messages and act on them.

---

## Kanban Workflow (MANDATORY)

When working on ANY kanban ticket:
1. **ALWAYS** run `/kanban-task` before starting work - it detects state and guides the full workflow
2. **NEVER** skip the lifecycle: claim → plan → checklist → coding → testing
3. **NEVER** mark checklist items as "done" - only John (PM/tester) moves testing → done
4. **ALWAYS** write continuation notes after every checklist transition
5. **ALWAYS** use `set_task_active` when starting work on a task

---

## Architecture Quick Reference

### System Layers

```
┌─────────────────────────────────────────────────────┐
│  MainForm.cs (3K LOC) - UI Host & Panel Orchestrator│
│  ├── TasksPanel/     (Kanban board - WebView2)      │
│  ├── ChatPanel/      (Inter-terminal messaging)     │
│  ├── ActivityPanel/  (Activity feed)                │
│  ├── ProfilePanel/   (Team member profiles)         │
│  ├── InboxPanel/     (Notifications)                │
│  ├── ProjectPanel/   (Project management)           │
│  └── LauncherPanel/  (Terminal launcher)            │
├─────────────────────────────────────────────────────┤
│  REST API (port 5050) - MultiTerminalRestServer.cs  │
│  ├── TasksController      /api/tasks                │
│  ├── MessagingController  /api/messaging            │
│  ├── ProjectContextController /api/projects/{id}/context │
│  └── ToolsController      /api (self-documenting)   │
├─────────────────────────────────────────────────────┤
│  MessageBroker.cs (4.2K LOC) - CENTRAL HUB          │
│  Routes messages, manages task cache, delivers       │
│  webhooks, fires events to UI panels                 │
├─────────────────────────────────────────────────────┤
│  Persistence Layer (SQLite)                          │
│  ├── TaskDatabase.cs      (2.7K LOC) - tasks.db     │
│  ├── SessionDatabase.cs   (2.4K LOC) - sessions     │
│  ├── ProjectDatabase.cs   - projects                 │
│  ├── PlanDatabase.cs      - plans & phases           │
│  └── MessageQueueDatabase.cs - reliable delivery     │
├─────────────────────────────────────────────────────┤
│  MCP Server (Node.js) - multiterminal-mcp/index.js   │
│  Exposes tools to Claude Code agents via MCP protocol│
└─────────────────────────────────────────────────────┘
```

### Critical Files (Read These First)

| File | LOC | Role |
|------|-----|------|
| `MCPServer/Services/MessageBroker.cs` | 4,254 | **Central hub.** Routes all messages, caches tasks/terminals/profiles in ConcurrentDictionaries, delivers webhooks, fires events. Everything flows through here. |
| `Services/TaskDatabase.cs` | 2,694 | **Persistence.** SQLite CRUD for tasks, helpers, profiles, activity, stale tracking. All tables defined here. |
| `MainForm.cs` | 3,059 | **UI host.** Creates/docks panels, wires events between panels and MessageBroker, manages terminal lifecycle. |
| `MCPServer/Models/KanbanTask.cs` | 403 | **Core model.** All task properties: status, assignee, checklists, plan, helpers, stale tracking, continuation notes. |

### Folder Map

| Folder | Purpose | Key Files |
|--------|---------|-----------|
| `Services/` | Business logic & SQLite persistence | TaskDatabase, SessionDatabase, ProjectService, ProjectDatabase, ProjectContextService, ProjectJsonMigrationService, TerminalSpawner, SettingsService |
| `MCPServer/Services/` | MCP server services | **MessageBroker**, PoolCoordinator, ActivityService, StaleTaskService, SpawnService |
| `MCPServer/Tools/` | MCP tool implementations | MessagingTools, TaskTools, PlanTools, HelperTools, ProfileTools, InboxTools |
| `MCPServer/Models/` | Data models (14 files) | KanbanTask, Plan, Message, InboxMessage, TeamMemberProfile, TaskSummary |
| `Models/` | App-level models (8+ files) | TerminalSessionInfo, SpawnedTeammate, ProjectRegistryEntry, Project, ProjectAgent, ProjectMcpServer, ProjectSpecialistAgent, ProjectPath, ProjectPromptEntry, ProjectSkill |
| `API/Controllers/` | REST endpoints | TasksController, MessagingController, ProjectContextController, ToolsController |
| `TasksPanel/` | Kanban board UI | TasksPanelDocument + TasksPanelControl (WebView2) |
| `ChatPanel/` | Messaging UI | ChatPanelDocument + ChatPanelControl (WebView2) |
| `ActivityPanel/` | Activity feed UI | ActivityPanelDocument (WebView2) |
| `ProfilePanel/` | Team profiles UI | ProfilePanelDocument (WebView2) |
| `InboxPanel/` | Notifications UI | InboxPanelDocument |
| `Terminal/` | Terminal hosting | ConPtyTerminal, WebViewTerminalRenderer |
| `Docking/` | Window layout | GridLayoutManager |
| `Dialogs/` | Modal dialogs (7) | ProjectManager, Settings, ChatHistory, etc. |
| `docs/` | Architecture docs (16) | Design specs, workflow guides, integration docs |

### Database Tables (TaskDatabase.cs)

| Table | Key Columns | Purpose |
|-------|-------------|---------|
| `tasks` | id, title, status, assignee, checklist_json, plan, continuation_notes | Kanban tasks |
| `task_helpers` | task_id, helper_name, added_by | Helper assignments |
| `team_member_profiles` | display_name, avatar_url, specialties, availability | Team profiles |
| `activity_feed` | activity_type, event_data, timestamp | Activity events |
| `user_inbox` | user_id, task_id, message_type | Notifications |
| `task_summaries` | task_id, summary_at, previous_status, new_status | Progress snapshots |
| `helper_sessions` | task_id, prompt, status | Helper session tracking |
| `terminal_activity` | terminal, status, activity | Terminal state |

### Database Tables (ProjectDatabase.cs — same tasks.db file)

| Table | Key Columns | Purpose |
|-------|-------------|---------|
| `projects` | id, name, description, path, source_path, deploy_path, build_command, git_repo_url, git_default_branch, git_auto_commit, is_pinned | Project core record (SQLite-only, replaces JSON registry) |
| `project_agents` | project_id, agent_name, role, preferred_model | Agents assigned to a project |
| `project_mcp_servers` | project_id, server_name, is_enabled | MCP servers configured for a project |
| `project_specialist_agents` | project_id, agent_type, is_enabled, custom_prompt | Specialist agents (verifier, devils-advocate, etc.) |
| `project_paths` | project_id, path_type, path_value, description | Named filesystem paths for a project |
| `project_prompts` | project_id, prompt_type, prompt_text, display_order | Stored prompts/instructions |
| `project_skills` | project_id, skill_name, is_enabled | Skills enabled for a project |

### Key Patterns

**Adding a UI Panel:**
1. Create `{Name}Panel/{Name}PanelDocument.cs` inheriting `DockContent` (set DockAreas, HideOnClose=true)
2. Create inner control with WebView2 or custom renderer
3. Add `Initialize(MessageBroker broker)` and `ApplyTheme(bool isDark)` methods
4. In MainForm: instantiate, call Initialize(), wire events, add toolbar toggle button
5. Follow existing panels (TasksPanel, ActivityPanel) as templates

**Adding a Backend Feature:**
1. Add model to `MCPServer/Models/`
2. Add persistence to `TaskDatabase.cs` (table + CRUD methods + migration)
3. Add routing to `MessageBroker.cs` (methods + events)
4. Add MCP tool to `MCPServer/Tools/` if agents need access
5. Add REST endpoint to `API/Controllers/` if HTTP access needed

**Adding an MCP Tool:**
1. Add method to appropriate file in `MCPServer/Tools/` (MessagingTools, TaskTools, etc.)
2. Tools delegate to MessageBroker which delegates to TaskDatabase
3. Return a Result type (Success + Error + context fields)

**MessageBroker Events (subscribe in MainForm for UI updates):**
- `MessageSent`, `TerminalRegistered`, `TasksUpdated`, `ActivityRecorded`, `InboxUpdated`

**Data Storage Patterns:**
- Checklists: JSON array in `checklist_json` → `[{"item":"...","status":"pending|coding|testing|done","notes":[...]}]`
- Plans: Markdown in `plan` field
- Continuation notes: Free text in `continuation_notes` (session handoff context)

**Project System Architecture (Phase 4 — SQLite-only):**
- Projects are stored in SQLite (`tasks.db`) via `ProjectDatabase.cs` — NOT in JSON files.
- `ProjectService.cs` still reads/writes `.claude/project.json` files on disk (for portability) and keeps a `projects.json` registry in `%APPDATA%\MultiTerminal`.
- `ProjectJsonMigrationService.cs` is a one-time migration tool: reads the JSON registry via `ProjectService`, writes to SQLite via `ProjectDatabase`. Run on startup.
- `ProjectContextService.cs` provides a single-call "everything you need" context object by joining all 7 project tables. Used by `GET /api/projects/{id}/context`.
- MessageBroker uses `ProjectService.GetAllRegisteredProjects()` as its project list source (JSON registry). Future refactor will switch this to ProjectDatabase.
- `ProjectContextController.cs` exposes `GET /api/projects/{id}/context` returning a full `ProjectContext` JSON payload for agent consumption.

### Task-Specific File Guide

| Working On | Read These Files |
|------------|-----------------|
| **Task/Kanban features** | KanbanTask.cs → TaskDatabase.cs → TaskTools.cs → TasksPanelControl.cs |
| **Messaging** | Message.cs → MessageBroker.cs → MessagingTools.cs → ChatPanelControl.cs |
| **Activity feed** | ActivityEvent.cs → ActivityService.cs → ActivityPanelDocument.cs |
| **Team profiles** | TeamMemberProfile.cs → ProfileTools.cs → ProfilePanelDocument.cs |
| **Notifications/Inbox** | InboxMessage.cs → InboxTools.cs → InboxPanelDocument.cs |
| **Plans/Checklists** | Plan.cs → PlanDatabase.cs → PlanTools.cs |
| **Helper system** | TaskHelper.cs → HelperTools.cs → HelperSession.cs |
| **Stale task tracking** | StaleTaskService.cs → TaskDatabase.GetStaleTasks() |
| **REST API** | MultiTerminalRestServer.cs → Controllers/ |
| **Terminal spawning** | TerminalSpawner.cs → SpawnedTeammate.cs |
| **Projects (read/write)** | Project.cs → ProjectDatabase.cs → ProjectContextService.cs → GET /api/projects/{id}/context |
| **Project migration** | ProjectJsonMigrationService.cs reads ProjectService JSON, writes to ProjectDatabase |

### Codebase Stats

- **~80 C# production files**, ~24K lines of code
- **4 SQLite databases** (tasks, sessions, projects, message queue)
- **14 data models**, **6 MCP tool files**, **3 REST controllers**
- **7 UI panels** (WebView2-based), **7 dialog windows**
- REST API on port 5050, MCP server via Node.js

### Keeping This Summary Current

**If you made any of these changes, update the relevant section above before finishing your task:**

- [ ] Added or removed a **UI panel or dialog** → Update System Layers diagram + Folder Map
- [ ] Added or removed a **database table or column** → Update Database Tables
- [ ] Added or removed a **service, MCP tool file, or REST endpoint** → Update Folder Map
- [ ] Added or removed a **folder** → Update Folder Map
- [ ] Added or removed a **MessageBroker event** → Update MessageBroker Events list
- [ ] Changed a **key pattern** (how to add panels, features, tools) → Update Key Patterns
- [ ] Significantly changed **LOC of a critical file** (>500 lines added/removed) → Update Critical Files table + Codebase Stats

This summary is loaded into every agent's context at startup. Stale info here wastes minutes of exploration per session.

---

## CRITICAL: Task Terminology

**There are TWO different task systems - do NOT confuse them:**

### 📋 **"Ticket" or "Kanban Task"** = User-Facing Work (Use This By Default!)

- **API:** MultiTerminal REST API (http://localhost:5050)
- **Visible:** YES - Shows in the UI kanban board (MainForm)
- **Persistent:** YES - Stored in TaskDatabase, survives sessions
- **Shared:** YES - All terminals and agents can see and work on them
- **When to use:** When the user asks to create/track work, add features, fix bugs, etc.

**User says any of these → Use kanban tickets:**
- "Create a task for..."
- "Add this to the board"
- "Track this work"
- "Create a ticket for..."
- Any work tracking request

**Example:**
```
User: "Create a task to add dark mode"
You: create_task(
  title="Add dark mode to the app",
  description="Implement dark mode theme switching",
  createdBy="YourName"
)
```

### 📝 **"Internal Task"** = Your Personal To-Do List (Rarely Needed)

- **Tools:** `TaskCreate`, `TaskUpdate`, `TaskList` (Claude Code built-in)
- **Visible:** NO - Only you see these in your context
- **Persistent:** NO - Lost when session ends
- **Shared:** NO - Other agents/terminals can't see them
- **When to use:** Only for tracking YOUR OWN steps within a complex task (rarely needed)

**Only use internal tasks when:**
- Breaking down YOUR work into sub-steps for yourself
- Tracking progress within a single session
- User explicitly says "add to YOUR task list" (rare)

**Example:**
```
You think: "This is complex, let me break it down for myself"
TaskCreate: "Research existing patterns"
TaskCreate: "Implement feature"
TaskCreate: "Write tests"
(These help YOU track progress, but user never sees them)
```

### ⚠️ **DEFAULT RULE: When in doubt, use KANBAN TICKETS**

Unless the user explicitly asks for your personal task list, always use kanban tickets. The user wants to SEE the work you're tracking!

## MultiTerminal MCP Tools

**Preferred method:** Use the MCP tools (clean interface, formatted output)

### Available Tools

**Task Management:**
```
# List all tasks
list_tasks(status="all")

# Create a new task
create_task(
  title="Task title",
  description="Task description",
  createdBy="YourName"
)

# Claim a task
claim_task(taskId="abc123", assignee="YourName")

# Update task status
update_task_status(taskId="abc123", status="in_progress", updatedBy="YourName")

# Delete a task
delete_task(taskId="abc123", deletedBy="YourName")
```

**Team Communication:**
```
# List active terminals
list_terminals()

# Register your terminal
register_terminal(name="YourName", docId="unique-id")

# Send a message
send_message(fromTerminalId="your-id", to="RecipientName", message="Hello!")

# Broadcast to all
broadcast_message(fromTerminalId="your-id", message="Hello everyone!")

# Get your messages
get_messages(terminalId="your-id")
```

**Note:** These MCP tools wrap the REST API at `http://localhost:5050`. See `MultiTerminal/API/README.md` for raw REST API documentation if needed.
