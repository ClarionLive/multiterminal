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
- `BrowserTabRequested`, `BranchOutcomeUpdated`
- `TaskActiveChanged` — fires after `SetTaskActive` swaps the assignee's active task. Args carry `AgentName`, `OldTaskId`+`OldWorktreePath` (nullable when no prior active task), `NewTaskId`+`NewWorktreePath` (nullable when no worktree materialized). Subscribed by MainForm which pushes a `{type:"task_active_changed",...}` JSON envelope over the agent's Claude Code Channel for the auto-cd hook (task c6ed236c).
- `WorktreePruning` — fires synchronously just before `WorktreeManager.PruneForTaskAsync` in the task-done flow. Args carry `TaskId`, `WorktreePath`, `RepoRoot`, `AgentName` (assignee, possibly null). Subscribed by MainForm which broadcasts a `{type:"worktree_pruning",...}` JSON envelope to EVERY connected terminal (via `GetAllConnectedTerminals`, not `GetTerminals` — subagents are not filtered out) so any agent with cwd inside the worktree can `cd` out before the rmdir attempt. The subscriber synchronously awaits deliveries with a 1.5s cap; that's the only sync point between broadcast and prune (task db4b18c6).

# Project System Architecture (Phase 4 -- SQLite-only)

- Projects stored in SQLite (`multiterminal.db`) via `ProjectDatabase.cs` -- NOT in JSON files.
- `ProjectService.cs` still reads/writes `.claude/project.json` on disk (portability) and keeps `projects.json` registry in `%APPDATA%\MultiTerminal`.
- `ProjectJsonMigrationService.cs` is a one-time migration: reads JSON registry via ProjectService, writes to SQLite via ProjectDatabase. Runs on startup.
- `ProjectContextService.cs` provides single-call context object joining all 7 project tables. Used by `GET /api/projects/{id}/context`.
- MessageBroker uses `ProjectService.GetAllRegisteredProjects()` as project list source (JSON registry). Future refactor switches to ProjectDatabase.
