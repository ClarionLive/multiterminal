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
- `TaskActiveChanged` — fires after `SetTaskActive` swaps the assignee's active task. Args carry `AgentName`, `OldTaskId`+`OldWorktreePath` (nullable when no prior active task), `NewTaskId`+`NewWorktreePath` (nullable when no worktree materialized). Args are **immutable** (constructor-set, get-only properties) so a misbehaving earlier in-proc subscriber can't rewrite identity or paths and redirect later subscribers — cycle-6 hardening for HUD rebind and the MainForm channel push, both of which consume the args directly (task dbbb8de2). Subscribed by MainForm which pushes a `{type:"task_active_changed",...}` JSON envelope over the agent's Claude Code Channel for the auto-cd hook (task c6ed236c), and by TerminalDocument which re-binds the HUD Git panel instead of trusting `NewWorktreePath`. Per-agent isolation (task bab81a92): `AgentName` is now the **acting** agent (the one that activated the task — may be a helper, not the assignee), and both subscribers resolve the worktree **agent-aware**: `broker.Worktrees.GetWorktreePathForTask(NewTaskId, AgentName)` with a canonical fallback, so a helper rebinds/cd's to its own `task/<id>--<slug>` worktree rather than the assignee's canonical one.
- `WorktreePruning` — fires synchronously just before `WorktreeManager.PruneForTaskAsync` in the task-done flow. Args carry `TaskId`, `WorktreePath`, `RepoRoot`, `AgentName` (assignee, possibly null). Subscribed by MainForm which broadcasts a `{type:"worktree_pruning",...}` JSON envelope so any agent with cwd inside the worktree can `cd` out before the rmdir attempt. The subscriber synchronously awaits deliveries with a 1.5s cap; that's the only sync point between broadcast and prune (task db4b18c6). **Audience (task d32c80eb):** the broadcast goes to `GetWorktreeEvictionAudience(taskId, assignee)` — the task assignee, its helpers, and ALL temporary `Agent *` subagents — NOT every connected terminal. Unrelated named peer terminals were rendering the raw control envelope as a visible `← multiterminal-channel` line; the visible render is Claude Code's channel feature (not per-message suppressible), so the fix narrows the recipient set. Subagents can't be attributed to a parent by name (`"Agent {label}"` encodes the Task-tool label, not the spawner — the spawner link lives only in `MULTITERMINAL_SPAWNER` env), so all subagents are kept (they're the shells most likely to be cwd-inside); only named bystanders are trimmed.
- `WorktreeReady` — fires AFTER both `PruneForTaskAsync` AND the post-prune auto-merge complete in the task-done flow. Fired from `PerformPostPruneMergeAndFireReady` in both the synchronous `UpdateTaskStatus` path AND the janitor's `TryDeferredPruneRetryAsync` path (so deferred prunes also notify subscribers when they eventually succeed). Args are **immutable** (`TaskId`, `WorktreePath`, `RepoRoot`, `AgentName`) — `WorktreeReadyEventArgs` is constructor-set with read-only properties so a misbehaving earlier subscriber can't redirect later subscribers at a different repository. Subscribed by `HudGitRenderer` which releases its cached `GitRepoService` for the pruned worktree path and rebinds to the post-merge repo root. Contract with `WorktreePruning`: pruning says "drop your live handles, the directory is about to disappear"; ready says "the new post-merge repo state is durable, refresh your view." Two events, two phases — subscribers can subscribe to either or both depending on whether they care about the pre-delete handle teardown or the post-merge rebind (task dbbb8de2).

# Project System Architecture (Phase 4 -- SQLite-only)

- Projects stored in SQLite (`multiterminal.db`) via `ProjectDatabase.cs` -- NOT in JSON files.
- `ProjectService.cs` still reads/writes `.claude/project.json` on disk (portability) and keeps `projects.json` registry in `%APPDATA%\MultiTerminal`.
- `ProjectJsonMigrationService.cs` is a one-time migration: reads JSON registry via ProjectService, writes to SQLite via ProjectDatabase. Runs on startup.
- `ProjectContextService.cs` provides single-call context object joining all 7 project tables. Used by `GET /api/projects/{id}/context`.
- MessageBroker uses `ProjectService.GetAllRegisteredProjects()` as project list source (JSON registry). Future refactor switches to ProjectDatabase.
