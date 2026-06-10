# TaskDatabase

> SQLite persistence for the kanban system. 21+ tables covering tasks, checklists, helpers, inbox, attachments, activity, summaries, and session tracking.

**Tags:** `core`, `persistence`, `sqlite`

## Key Files

- `Services/TaskDatabase.cs` (6699 LOC)
  - SQLite CRUD for kanban tasks, helpers, profiles, activity, inbox, summaries, and stale tracking
- `MCPServer/Models/KanbanTask.cs` (500 LOC)
  - Core kanban task model with lifecycle (todo/in_progress/done), checklist state machine, stale tracking, helpers, plans, summaries, continuations.
- `MCPServer/Models/Plan.cs` (362 LOC)
  - Plan and phase models with checklist items, notes history, assignments, and decisions for task-centric workflow management.
- `MCPServer/Models/InboxMessage.cs` (117 LOC)
  - Notification model for user inbox: auto-generated from checklist transitions and escalations with optional reply/read tracking.
- `MCPServer/Models/TaskAttachment.cs` (76 LOC)
  - Attachment model for images on tasks/checklist items: metadata with disk storage and MIME type tracking.
- `MCPServer/Models/TaskSummary.cs` (64 LOC)
  - Progress summary model capturing task context at status changes: work completed, next steps, blockers, notes, with auto-generation flag.
- `MCPServer/Models/TaskHelper.cs` (55 LOC)
  - Helper assignment model for team collaboration: tracks which terminal helps on a task and who added them.

## Key Classes

- **TaskDatabase** (class) — `Services/TaskDatabase.cs:13`
- **ChatMessageRecord** (class) — `Services/TaskDatabase.cs:4889`
- **MessageSearchResult** (class) — `Services/TaskDatabase.cs:4902`
- **KanbanTask** (class) — `MCPServer/Models/KanbanTask.cs:9`
- **CreateTaskResult** (class) — `MCPServer/Models/KanbanTask.cs:210`
- **ClaimTaskResult** (class) — `MCPServer/Models/KanbanTask.cs:220`
- **ComplexitySuggestion** (class) — `MCPServer/Models/KanbanTask.cs:246`
- **ComplexityAnalysisResult** (class) — `MCPServer/Models/KanbanTask.cs:272`
- **PlanSuggestionResult** (class) — `MCPServer/Models/KanbanTask.cs:285`
- **SuggestedPhase** (class) — `MCPServer/Models/KanbanTask.cs:298`
- **UpdateTaskStatusResult** (class) — `MCPServer/Models/KanbanTask.cs:308`
- **UpdateTaskResult** (class) — `MCPServer/Models/KanbanTask.cs:317`
- **DeleteTaskResult** (class) — `MCPServer/Models/KanbanTask.cs:326`
- **AddHelperResult** (class) — `MCPServer/Models/KanbanTask.cs:335`
- **RemoveHelperResult** (class) — `MCPServer/Models/KanbanTask.cs:345`
- **GetHelpersResult** (class) — `MCPServer/Models/KanbanTask.cs:355`
- **RequestHelpResult** (class) — `MCPServer/Models/KanbanTask.cs:365`
- **RespondStaleResult** (class) — `MCPServer/Models/KanbanTask.cs:375`
- **UpdateChecklistItemResult** (class) — `MCPServer/Models/KanbanTask.cs:385`
- **SetTaskActiveResult** (class) — `MCPServer/Models/KanbanTask.cs:399`

## Key Methods

- `TaskDatabase.GetDatabasePath` — `Services/TaskDatabase.cs:25`
- `TaskDatabase.LoadAllTasks` — `Services/TaskDatabase.cs:1053`
- `TaskDatabase.SaveTask` — `Services/TaskDatabase.cs:1118`
- `TaskDatabase.DeleteTask` — `Services/TaskDatabase.cs:1179`
- `TaskDatabase.AddRelationship` — `Services/TaskDatabase.cs:1194`
- `TaskDatabase.RemoveRelationshipsBetween` — `Services/TaskDatabase.cs:1210`
- `TaskDatabase.GetRelationshipsForTask` — `Services/TaskDatabase.cs:1223`
- `TaskDatabase.DeleteRelationshipsForTask` — `Services/TaskDatabase.cs:1250`
- `TaskDatabase.AddFileLink` — `Services/TaskDatabase.cs:1262`
- `TaskDatabase.RemoveFileLink` — `Services/TaskDatabase.cs:1280`
- `TaskDatabase.GetFileLinksForTask` — `Services/TaskDatabase.cs:1289`
- `TaskDatabase.DeleteFileLinksForTask` — `Services/TaskDatabase.cs:1318`
- `TaskDatabase.UpdateTask` — `Services/TaskDatabase.cs:1332`
- `TaskDatabase.UpdateTaskAssignee` — `Services/TaskDatabase.cs:1353`
- `TaskDatabase.DeleteAllTasks` — `Services/TaskDatabase.cs:1372`
- `TaskDatabase.LoadAllProjects` — `Services/TaskDatabase.cs:1385`
- `TaskDatabase.SaveProject` — `Services/TaskDatabase.cs:1417`
- `TaskDatabase.UpdateProject` — `Services/TaskDatabase.cs:1443`
- `TaskDatabase.DeleteProject` — `Services/TaskDatabase.cs:1463`
- `TaskDatabase.DeleteAllProjects` — `Services/TaskDatabase.cs:1475`
- `TaskDatabase.SaveTaskSummary` — `Services/TaskDatabase.cs:1490`
- `TaskDatabase.GetTaskSummaries` — `Services/TaskDatabase.cs:1520`
- `TaskDatabase.GetLatestSummary` — `Services/TaskDatabase.cs:1549`
- `TaskDatabase.GetAllRecentSummaries` — `Services/TaskDatabase.cs:1575`
- `TaskDatabase.GetPendingSummaries` — `Services/TaskDatabase.cs:1602`

## External Callers

> Code outside this subsystem that calls into it.

- `MessageBroker.LoadPersistedTasks` — `MCPServer/Services/MessageBroker.cs:427`
- `PlanDatabase.GenerateTaskStackSection` — `Services/PlanDatabase.cs:633`
- `StaleTaskService.GetStaleTasksForTerminal` — `MCPServer/Services/StaleTaskService.cs:86`
- `MessageBroker.AssignChecklistItem` — `MCPServer/Services/MessageBroker.cs:3058`
- `MessageBroker.CreateTask` — `MCPServer/Services/MessageBroker.cs:2138`
- `MessageBroker.MakeTaskActive` — `MCPServer/Services/MessageBroker.cs:2429`
- `MessageBroker.PauseTaskWithSummary` — `MCPServer/Services/MessageBroker.cs:2387`
- `MessageBroker.QueueTaskForTerminal` — `MCPServer/Services/MessageBroker.cs:2356`
- `MessageBroker.RespondToStale` — `MCPServer/Services/MessageBroker.cs:3654`
- `MessageBroker.SaveTask` — `MCPServer/Services/MessageBroker.cs:2106`
- `MessageBroker.SetAutoStatus` — `MCPServer/Services/MessageBroker.cs:2800`
- `MessageBroker.SetTaskActive` — `MCPServer/Services/MessageBroker.cs:3188`
- `MessageBroker.TransitionChecklistItem` — `MCPServer/Services/MessageBroker.cs:2957`
- `MessageBroker.UpdateTask` — `MCPServer/Services/MessageBroker.cs:2673`
- `MessageBroker.UpdateTaskChecklist` — `MCPServer/Services/MessageBroker.cs:2709`

## Gotchas

- Checklist items stored as [item, status, notes[]]
- Activity is append-only (no deletes)
- FTS5 clamped to 500 chars
- StaleTasks computed at query time
- ChecklistJson stored as JSON string, GetChecklist normalizes legacy items, SetChecklist syncs Done flag, StaleLevel: 0=fresh/1=day7/2=day14, AutoStatus derives from checklist positions
- Id auto-generated as 8-char GUID substring, AddedAt defaults to UtcNow
- IsAutoGenerated marks AI-generated summaries, PendingEnhancement allows user refinement before next status change, TriggeredBy: status_change/checkpoint/session_end
- Id generated as 12-char GUID substring, StoredFileName prefixed with ID to avoid collisions, ChecklistItemIndex=-1 means task-level attachment
- Type field: 'ready_for_testing', 'escalation', 'task_complete', 'helper_request', ChecklistItemIndex nullable for task-level notifications
- ChecklistItem normalizes legacy Done flag to Status, Status values: pending/coding/testing/done, CycleCount auto-increments on testing→coding, SortOrder for UI positioning, Notes history tracks who/when/what for each transition

---
_Generated 2026-06-10T17:01:21.1805965Z · [Back to index](./index.md)_
