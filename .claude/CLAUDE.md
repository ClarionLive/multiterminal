# MultiTerminal Codebase Reference

A multi-agent coordination system for Claude Code. WinForms desktop app (C#/.NET) with integrated REST API (port 5050), MCP server, and WebView2-based UI panels.

> Agent behavioral instructions (kanban workflow, MCP tools, messaging, task terminology) are in the MultiTerminal plugin CLAUDE.md. This file is only the codebase reference for working on MT source code.

---

## Architecture Quick Reference

### System Layers

```
┌──────────────────────────────────────────────────────────┐
│  MainForm.cs (5.2K LOC) - UI Host & Panel Orchestrator   │
│  ├── TasksPanel/      (Kanban board - WebView2)          │
│  ├── ChatPanel/       (Inter-terminal messaging)         │
│  ├── ActivityPanel/   (Activity feed)                    │
│  ├── ProfilePanel/    (Team member profiles)             │
│  ├── InboxPanel/      (Notifications)                    │
│  ├── ProjectPanel/    (Project management)               │
│  ├── LauncherPanel/   (Terminal launcher)                │
│  ├── OfficePanel/     (Agent office - spawn/manage)      │
│  ├── AgentPanel/      (Live agent transcript viewer)     │
│  ├── DebugPanel/      (Debug log viewer)                 │
│  └── FilePreviewPanel/(File preview - WebView2)          │
├──────────────────────────────────────────────────────────┤
│  REST API (port 5050) - MultiTerminalRestServer.cs       │
│  21 controllers in API/Controllers/ — key ones:          │
│  ├── TasksController        /api/tasks (+ checklist,     │
│  │                          files, relationships, reports)│
│  ├── MessagingController    /api/messaging               │
│  ├── ProjectContextController /api/projects              │
│  ├── SessionLineageController /api/session-lineage       │
│  ├── BrowserTabsController  /api/browser-tabs            │
│  ├── CompanionController    /api/companions              │
│  ├── GatewayController      /api/gateway                 │
│  ├── NotificationsController /api/notifications          │
│  └── ToolsController        /api (self-documenting)      │
├──────────────────────────────────────────────────────────┤
│  MessageBroker.cs (5.5K LOC) - CENTRAL HUB               │
│  Routes messages, manages task cache, delivers            │
│  webhooks, fires 19 events to UI panels                   │
├──────────────────────────────────────────────────────────┤
│  Persistence Layer (SQLite)                               │
│  ├── TaskDatabase.cs      (4.6K LOC) - multiterminal.db  │
│  │   (tasks, sessions, knowledge, reports, profiles, etc.)│
│  ├── ProjectDatabase.cs   - projects (7 tables)           │
│  ├── PlanDatabase.cs      - plans & phases                │
│  └── MessageQueueDatabase.cs - reliable delivery          │
├──────────────────────────────────────────────────────────┤
│  MCP Server (Node.js) - %APPDATA%/multiterminal/mcp      │
│  80 tools exposed to Claude Code agents via MCP protocol  │
└──────────────────────────────────────────────────────────┘
```

### Critical Files (Read These First)

| File | LOC | Role |
|------|-----|------|
| `MCPServer/Services/MessageBroker.cs` | 5,512 | **Central hub.** Routes all messages, caches tasks/terminals/profiles in ConcurrentDictionaries, delivers webhooks, fires 18 events. Everything flows through here. |
| `Services/TaskDatabase.cs` | 4,621 | **Persistence.** SQLite CRUD for 21+ tables: tasks, sessions, knowledge, reports, profiles, activity, attachments, and more. |
| `MainForm.cs` | 5,162 | **UI host.** Creates/docks 11 panels, wires events between panels and MessageBroker, manages terminal lifecycle. |
| `MCPServer/Models/KanbanTask.cs` | 476 | **Core model.** All task properties: status, assignee, checklists, plan, helpers, stale tracking, continuation notes. |

### Folder Map

| Folder | Purpose | Key Files |
|--------|---------|-----------|
| `Services/` | Business logic & SQLite persistence | TaskDatabase, ProjectService, ProjectDatabase, ProjectContextService, KnowledgeDatabase, TerminalSpawner, SettingsService, CompanionProcessManager, GatewayIntegrationService, OwnerProfileService, RipgrepService, TerminalStreamService, SessionLineageService, SessionSyncService, TranscriptTailer |
| `MCPServer/Services/` | MCP server services | **MessageBroker**, PoolCoordinator, ActivityService, StaleTaskService, SpawnService, HttpWebhookService, SessionDiscovery, ComplexityDetector, SummaryService |
| `MCPServer/Models/` | Data models (17 files) | KanbanTask, Plan, Message, InboxMessage, TeamMemberProfile, TaskSummary, ActivityEvent, TaskHelper, ComplexityStats, CodeDigest, KnowledgeEntry, TaskAttachment |
| `Models/` | App-level models (10 files) | TerminalSessionInfo, SpawnedTeammate, ProjectRegistryEntry, Project, ProjectAssociations, OwnerProfile, CompanionProcess, ClaudeCommand, DebugLogEntry, AgentMessage |
| `API/Controllers/` | REST endpoints (21 controllers) | TasksController, MessagingController, ProjectContextController, TaskReportsController, BrowserTabsController, SessionLineageController, CompanionController, GatewayController, NotificationsController, OwnerProfileController, SpawnController, TeamController, DebugController, AgentStatsController, RipgrepController, TerminalStreamController, XamlPreviewController, OfficeController, KnowledgeController, AgentPanelsController, ToolsController |
| `TasksPanel/` | Kanban board UI | TasksPanelDocument + TasksPanelControl (WebView2) |
| `ChatPanel/` | Messaging UI | ChatPanelDocument + ChatPanelControl (WebView2) |
| `ActivityPanel/` | Activity feed UI | ActivityPanelDocument (WebView2) |
| `ProfilePanel/` | Team profiles UI | ProfilePanelDocument (WebView2) |
| `InboxPanel/` | Notifications UI | InboxPanelDocument |
| `OfficePanel/` | Agent office UI | OfficePanelDocument + OfficePanelRenderer (WebView2) |
| `AgentPanel/` | Live agent transcript | AgentPanelDocument + AgentPanelControl (WebView2) |
| `Panels/` | Misc panels | DebugPanel |
| `FilePreviewPanel/` | File preview UI | FilePreviewPanelDocument (WebView2) |
| `StartScreen/` | Welcome/start screen | StartScreenControl (WebView2) |
| `TaskLifecycleBoard/` | Task lifecycle view | TaskLifecycleBoardForm (WebView2) |
| `Controls/` | Reusable controls | TerminalControl, TerminalStatusBarRenderer, EmbeddedAgentPanel, HudTabContainer |
| `Terminal/` | Terminal hosting | ConPtyTerminal, WebViewTerminalRenderer |
| `Docking/` | Window layout | GridLayoutManager, TerminalDocument, ProjectPanelDocument, LauncherPanelDocument |
| `Dialogs/` | Modal dialogs (11) | ProjectManager, Settings, ChatHistory, NewProjectWpf, OwnerProfile, SavePrompt, RenameTab, SessionViewer, EditProject, IdentityPicker, About |
| `docs/` | Architecture docs | Design specs, workflow guides, integration docs |
| `installer/` | Distribution | MultiTerminal.iss (Inno Setup), build-installer.ps1, post-install.js, post-uninstall.js |
| `tools/` | Bundled tools | rg.exe (ripgrep for code search) |

### Database Tables (TaskDatabase.cs)

| Table | Key Columns | Purpose |
|-------|-------------|---------|
| `tasks` | id, title, status, assignee, checklist_json, plan, continuation_notes | Kanban tasks |
| `task_helpers` | task_id, helper_name, added_by | Helper assignments |
| `task_relationships` | task_id, related_task_id, relationship_type | Task dependencies/links |
| `task_file_links` | task_id, file_path, added_by, description | Files associated with tasks |
| `task_reports` | id, task_id, invocation_id, agent_name, report_type, report_content, verdict, score, created_at, created_by | Persisted agent reports (HTML/markdown) linked to tasks |
| `task_summaries` | task_id, summary_at, previous_status, new_status | Progress snapshots |
| `task_attachments` | id, task_id, checklist_item_index, file_name, content_type, image_data | Images/files attached to checklist items |
| `team_member_profiles` | display_name, avatar_url, specialties, availability | Team profiles |
| `owner_profile` | key, value | Owner identity (git config, GitHub token) |
| `activity_feed` | activity_type, event_data, timestamp | Activity events |
| `user_inbox` | user_id, task_id, message_type | Notifications |
| `notification_events` | id, source, event_type, title, body, timestamp | Push notification events |
| `helper_sessions` | task_id, prompt, status | Helper session tracking |
| `helper_messages` | task_id, helper_name, role, content | Helper conversation history |
| `terminal_activity` | terminal, status, activity | Terminal state |
| `agent_invocations` | id, agent_name, task_id, started_at, duration_ms | Agent performance tracking |
| `chat_messages` | id, from_terminal, to_terminal, message, timestamp | Persistent chat history |
| `session_lineage` | session_id, agent_name, session_type, summary, session_file_path | Session history & lineage chains |
| `session_messages` | session_id, role, content, tool_name, timestamp | Extracted messages from JSONL files |
| `session_agent_map` | session_id, agent_name, is_active | Hook-written map: which agent owns each session (used by auto-sync) |
| `knowledge_entries` | id, topic, content, source, created_at | Institutional knowledge base |
| `code_digests` | id, file_path, digest, created_at | Code digest summaries |
| `complexity_decisions` | id, task_id, complexity, reasoning | Task complexity assessments |

### Database Tables (ProjectDatabase.cs — same multiterminal.db file)

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
4. Add MCP tool to the Node.js MCP server (`%APPDATA%/multiterminal/mcp/index.js`) if agents need access
5. Add REST endpoint to `API/Controllers/` if HTTP access needed

**Adding an MCP Tool:**
1. Add tool definition in the Node.js MCP server (`%APPDATA%/multiterminal/mcp/index.js`)
2. Tools call the REST API at localhost:5050, which delegates to MessageBroker → TaskDatabase
3. Return formatted result with success/error context

**MessageBroker Events (subscribe in MainForm or panels for UI updates):**
- `MessageSent`, `TerminalRegistered`, `TerminalDisconnected`, `TasksUpdated`, `TaskClaimed`
- `ActivityRecorded`, `InboxUpdated`, `PlanUpdated`, `ProjectsUpdated`, `ProfilesUpdated`
- `HelperSessionUpdated`, `HelperMessageLogged`, `OfficeAgentSpawned`, `OfficeAgentDeparted`
- `AgentPanelCloseRequested`, `ReportSaved`, `NotificationReceived`, `SessionLineageUpdated`
- `BrowserTabRequested`

**Data Storage Patterns:**
- Checklists: JSON array in `checklist_json` → `[{"item":"...","status":"pending|coding|testing|done","notes":[...]}]`
- Plans: Markdown in `plan` field
- Continuation notes: Free text in `continuation_notes` (session handoff context)

**Project System Architecture (Phase 4 — SQLite-only):**
- Projects are stored in SQLite (`multiterminal.db`) via `ProjectDatabase.cs` — NOT in JSON files.
- `ProjectService.cs` still reads/writes `.claude/project.json` files on disk (for portability) and keeps a `projects.json` registry in `%APPDATA%\MultiTerminal`.
- `ProjectJsonMigrationService.cs` is a one-time migration tool: reads the JSON registry via `ProjectService`, writes to SQLite via `ProjectDatabase`. Run on startup.
- `ProjectContextService.cs` provides a single-call "everything you need" context object by joining all 7 project tables. Used by `GET /api/projects/{id}/context`.
- MessageBroker uses `ProjectService.GetAllRegisteredProjects()` as its project list source (JSON registry). Future refactor will switch this to ProjectDatabase.
- `ProjectContextController.cs` exposes `GET /api/projects/{id}/context` returning a full `ProjectContext` JSON payload for agent consumption.

### Task-Specific File Guide

| Working On | Read These Files |
|------------|-----------------|
| **Task/Kanban features** | KanbanTask.cs → TaskDatabase.cs → TasksController.cs → TasksPanelControl.cs |
| **Messaging** | Message.cs → MessageBroker.cs → MessagingController.cs → ChatPanelControl.cs |
| **Activity feed** | ActivityEvent.cs → ActivityService.cs → ActivityPanelDocument.cs |
| **Team profiles** | TeamMemberProfile.cs → TeamController.cs → ProfilePanelDocument.cs |
| **Notifications/Inbox** | InboxMessage.cs → NotificationsController.cs → InboxPanelDocument.cs |
| **Plans/Checklists** | Plan.cs → PlanDatabase.cs → TasksController.cs |
| **Helper system** | TaskHelper.cs → MessageBroker.cs → OfficeController.cs |
| **Task reports/Pipeline** | TaskReportsController.cs → TaskDatabase.cs → TasksPanelControl.cs |
| **Stale task tracking** | StaleTaskService.cs → TaskDatabase.GetStaleTasks() |
| **REST API** | MultiTerminalRestServer.cs → Controllers/ (21 controllers) |
| **Terminal spawning** | TerminalSpawner.cs → SpawnController.cs → SpawnedTeammate.cs |
| **Terminal streaming** | TerminalStreamService.cs → TerminalStreamController.cs (WebSocket) |
| **Browser tabs (HUD)** | BrowserTabsController.cs → HudTabContainer/ |
| **Companion processes** | CompanionProcessManager.cs → CompanionController.cs → CompanionProcess.cs |
| **MCP Gateway** | GatewayIntegrationService.cs → GatewayController.cs |
| **Knowledge base** | KnowledgeDatabase.cs → KnowledgeController.cs |
| **Owner profile** | OwnerProfileService.cs → OwnerProfileController.cs → OwnerProfileDialog.xaml |
| **Projects (read/write)** | Project.cs → ProjectDatabase.cs → ProjectContextService.cs → GET /api/projects/{id}/context |
| **Session lineage** | SessionLineageService.cs → SessionLineageController.cs → SessionSyncService.cs |

### Codebase Stats

- **~150 C# production files**, ~65K lines of code
- **3 SQLite databases** (multiterminal.db for tasks/sessions/knowledge/profiles, projects in same db, message queue separate)
- **17 data models**, **21 REST controllers**, **80 MCP tools**
- **11 UI panels** (WebView2-based), **11 dialog windows**
- REST API on port 5050, MCP server via Node.js, MCP Gateway (McpGateway.exe)

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
