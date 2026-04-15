---
paths:
  - MCPServer/Services/MessageBroker.cs
  - MainForm.cs
  - "*Panel*/**"
---

# MessageBroker Events

Subscribe in MainForm or panels for UI updates:

- `MessageSent`, `TerminalRegistered`, `TerminalDisconnected`
- `TasksUpdated`, `TaskClaimed`
- `ActivityRecorded`, `InboxUpdated`, `PlanUpdated`
- `ProjectsUpdated`, `ProfilesUpdated`
- `HelperSessionUpdated`, `HelperMessageLogged`
- `OfficeAgentSpawned`, `OfficeAgentDeparted`
- `AgentPanelCloseRequested`, `ReportSaved`
- `NotificationReceived`, `SessionLineageUpdated`
- `BrowserTabRequested`

# Project System Architecture (Phase 4 -- SQLite-only)

- Projects stored in SQLite (`multiterminal.db`) via `ProjectDatabase.cs` -- NOT in JSON files.
- `ProjectService.cs` still reads/writes `.claude/project.json` on disk (portability) and keeps `projects.json` registry in `%APPDATA%\MultiTerminal`.
- `ProjectJsonMigrationService.cs` is a one-time migration: reads JSON registry via ProjectService, writes to SQLite via ProjectDatabase. Runs on startup.
- `ProjectContextService.cs` provides single-call context object joining all 7 project tables. Used by `GET /api/projects/{id}/context`.
- MessageBroker uses `ProjectService.GetAllRegisteredProjects()` as project list source (JSON registry). Future refactor switches to ProjectDatabase.
