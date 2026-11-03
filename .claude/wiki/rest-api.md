# REST API

> HTTP server exposing the REST API on port 5050 across the API/Controllers/ set. Bridges MCP tools and UI panels to MessageBroker and databases.

**Tags:** `api`, `http`, `integration`

## Key Files

- `API/Controllers/TasksController.cs` (956 LOC)
  - Core REST API for kanban tasks: CRUD, status, assignment, helpers, checklist transitions, plans, summaries, inbox, attachments.
- `API/MultiTerminalRestServer.cs` (478 LOC)
  - ASP.NET Core REST API server host (port 5050) with service wiring, periodic timers for stale tasks/agents, MCP registry seeding.
- `API/Controllers/ProjectContextController.cs` (415 LOC)
  - REST endpoint providing complete project context including agents, MCP servers, paths, prompts, and skills in one call.
- `API/Controllers/SessionLineageController.cs` (402 LOC)
  - REST endpoints for session lineage: importing transcripts, querying chains, FTS search, and sync from project folders.
- `API/Controllers/KnowledgeController.cs` (359 LOC)
  - REST endpoints for institutional memory system: knowledge entries and code digests with search, CRUD, and stale detection.
- `API/Controllers/DigestController.cs` (349 LOC)
- `API/Controllers/CodeGraphController.cs` (253 LOC)
- `API/Controllers/MessagingController.cs` (221 LOC)
  - REST endpoints for inter-terminal messaging: register, send, broadcast, get messages, list terminals, disconnect.
- `API/Controllers/BranchMetadataController.cs` (203 LOC)
- `API/Controllers/DebugController.cs` (199 LOC)
  - REST endpoints for querying and managing debug logs with filtering, pagination, pause/resume, and status.
- `API/Controllers/PermissionRelayTestController.cs` (195 LOC)
- `API/Controllers/BrowserTabsController.cs` (190 LOC)
- `API/Controllers/WikiController.cs` (188 LOC)
- `API/Controllers/SessionMemoryController.cs` (172 LOC)
- `API/Controllers/NotificationsController.cs` (168 LOC)
- `API/Controllers/WorktreesController.cs` (147 LOC)
- `API/Controllers/ElicitationsController.cs` (142 LOC)
- `API/Controllers/RipgrepController.cs` (121 LOC)
- `API/Controllers/SpawnController.cs` (119 LOC)
  - REST endpoint for spawning headless AgentProcess with piped stdin/stdout for agent panel conversations.
- `API/Controllers/TeamController.cs` (116 LOC)
  - REST endpoint for team roster retrieval with merged profile data: names, instructions, roles, skills.
- `API/Controllers/GatewayController.cs` (113 LOC)
- `API/Controllers/TaskReportsController.cs` (93 LOC)
- `API/Controllers/XamlPreviewController.cs` (91 LOC)
- `API/Controllers/AgentStatsController.cs` (90 LOC)
- `API/Controllers/OfficeController.cs` (90 LOC)
  - REST endpoints for Office Panel: agent spawning/departing animations, listing, and cleanup of stale ghost agents.
- `API/Controllers/TerminalStreamController.cs` (88 LOC)
- `API/Controllers/RemoteModeController.cs` (66 LOC)
- `API/Controllers/ToolsController.cs` (62 LOC)
  - Self-documenting API endpoint that lists all available REST endpoints with HTTP methods and descriptions.
- `API/Controllers/TerminalStatsController.cs` (61 LOC)
- `API/Controllers/OwnerProfileController.cs` (53 LOC)
- `API/Controllers/AgentPanelsController.cs` (37 LOC)
  - REST endpoint for closing agent panel windows when subagents finish execution.
- `API/Controllers/SettingsController.cs` (35 LOC)
- `API/Controllers/CompanionController.cs` (27 LOC)

## Key Classes

- **MultiTerminalRestServer** (class) — `API/MultiTerminalRestServer.cs:18`
- **TasksController** (class) — `API/Controllers/TasksController.cs:12`
- **CreateTaskRequest** (class) — `API/Controllers/TasksController.cs:728`
- **UpdateStatusRequest** (class) — `API/Controllers/TasksController.cs:738`
- **AssignTaskRequest** (class) — `API/Controllers/TasksController.cs:744`
- **AddHelperRequest** (class) — `API/Controllers/TasksController.cs:749`
- **UpdateChecklistRequest** (class) — `API/Controllers/TasksController.cs:755`
- **TransitionChecklistRequest** (class) — `API/Controllers/TasksController.cs:760`
- **AssignChecklistRequest** (class) — `API/Controllers/TasksController.cs:767`
- **UpdateContinuationRequest** (class) — `API/Controllers/TasksController.cs:772`
- **UpdatePlanRequest** (class) — `API/Controllers/TasksController.cs:778`
- **UpdateSummaryRequest** (class) — `API/Controllers/TasksController.cs:784`
- **SetActiveRequest** (class) — `API/Controllers/TasksController.cs:791`
- **ReplyToInboxRequest** (class) — `API/Controllers/TasksController.cs:796`
- **AddAttachmentRequest** (class) — `API/Controllers/TasksController.cs:801`
- **AddRelationshipRequest** (class) — `API/Controllers/TasksController.cs:810`
- **LinkFileRequest** (class) — `API/Controllers/TasksController.cs:817`
- **UnlinkFileRequest** (class) — `API/Controllers/TasksController.cs:826`
- **MessagingController** (class) — `API/Controllers/MessagingController.cs:10`
- **RegisterTerminalRequest** (class) — `API/Controllers/MessagingController.cs:146`

## Key Methods

- `MultiTerminalRestServer.StartAsync` — `API/MultiTerminalRestServer.cs:101`
- `MultiTerminalRestServer.StopAsync` — `API/MultiTerminalRestServer.cs:308`
- `TasksController.ListTasks` — `API/Controllers/TasksController.cs:28`
- `TasksController.GetTask` — `API/Controllers/TasksController.cs:44`
- `TasksController.GetMyActiveTask` — `API/Controllers/TasksController.cs:58`
- `TasksController.CreateTask` — `API/Controllers/TasksController.cs:100`
- `TasksController.UpdateStatus` — `API/Controllers/TasksController.cs:122`
- `TasksController.DeleteTask` — `API/Controllers/TasksController.cs:135`
- `TasksController.AssignTask` — `API/Controllers/TasksController.cs:145`
- `TasksController.AddHelper` — `API/Controllers/TasksController.cs:158`
- `TasksController.RemoveHelper` — `API/Controllers/TasksController.cs:171`
- `TasksController.UpdateChecklist` — `API/Controllers/TasksController.cs:187`
- `TasksController.TransitionChecklistItem` — `API/Controllers/TasksController.cs:200`
- `TasksController.AssignChecklistItem` — `API/Controllers/TasksController.cs:221`
- `TasksController.UpdateContinuation` — `API/Controllers/TasksController.cs:234`
- `TasksController.UpdatePlan` — `API/Controllers/TasksController.cs:247`
- `TasksController.UpdateSummary` — `API/Controllers/TasksController.cs:260`
- `TasksController.SetTaskActive` — `API/Controllers/TasksController.cs:273`
- `TasksController.GetPickableTasks` — `API/Controllers/TasksController.cs:292`
- `TasksController.GetTaskDetail` — `API/Controllers/TasksController.cs:349`
- `TasksController.AddRelationship` — `API/Controllers/TasksController.cs:402`
- `TasksController.RemoveRelationship` — `API/Controllers/TasksController.cs:415`
- `TasksController.GetRelationships` — `API/Controllers/TasksController.cs:428`
- `TasksController.LinkFile` — `API/Controllers/TasksController.cs:445`
- `TasksController.UnlinkFile` — `API/Controllers/TasksController.cs:458`

## Routes

- `GET` `/api/tasks` — `API/Controllers/TasksController.cs:28`
- `GET` `/api/tasks/{taskId}` — `API/Controllers/TasksController.cs:49`
- `GET` `/api/tasks/active/{agentName}` — `API/Controllers/TasksController.cs:63`
- `POST` `/api/tasks` — `API/Controllers/TasksController.cs:105`
- `POST` `/api/tasks/quick` — `API/Controllers/TasksController.cs:130`
- `PATCH` `/api/tasks/{taskId}/status` — `API/Controllers/TasksController.cs:191`
- `PATCH` `/api/tasks/{taskId}/title` — `API/Controllers/TasksController.cs:207`
- `PATCH` `/api/tasks/{taskId}/order` — `API/Controllers/TasksController.cs:226`
- `DELETE` `/api/tasks/{taskId}` — `API/Controllers/TasksController.cs:248`
- `POST` `/api/tasks/{taskId}/assign` — `API/Controllers/TasksController.cs:258`
- `POST` `/api/tasks/{taskId}/helpers` — `API/Controllers/TasksController.cs:271`
- `DELETE` `/api/tasks/{taskId}/helpers/{helperName}` — `API/Controllers/TasksController.cs:284`
- `PATCH` `/api/tasks/{taskId}/checklist` — `API/Controllers/TasksController.cs:300`
- `POST` `/api/tasks/{taskId}/checklist/append` — `API/Controllers/TasksController.cs:313`
- `POST` `/api/tasks/{taskId}/checklist/{itemIndex}/transition` — `API/Controllers/TasksController.cs:326`
- `POST` `/api/tasks/{taskId}/checklist/{itemIndex}/assign` — `API/Controllers/TasksController.cs:347`
- `PATCH` `/api/tasks/{taskId}/continuation` — `API/Controllers/TasksController.cs:360`
- `PATCH` `/api/tasks/{taskId}/plan` — `API/Controllers/TasksController.cs:373`
- `PATCH` `/api/tasks/{taskId}/summary` — `API/Controllers/TasksController.cs:386`
- `POST` `/api/tasks/{taskId}/activate` — `API/Controllers/TasksController.cs:403`
- `GET` `/api/tasks/pickable/{agentName}` — `API/Controllers/TasksController.cs:491`
- `GET` `/api/tasks/{taskId}/detail` — `API/Controllers/TasksController.cs:548`
- `POST` `/api/tasks/{taskId}/relationships` — `API/Controllers/TasksController.cs:601`
- `DELETE` `/api/tasks/{taskId}/relationships/{relatedTaskId}` — `API/Controllers/TasksController.cs:614`
- `GET` `/api/tasks/{taskId}/relationships` — `API/Controllers/TasksController.cs:627`
- `POST` `/api/tasks/{taskId}/files` — `API/Controllers/TasksController.cs:644`
- `POST` `/api/tasks/{taskId}/files/unlink` — `API/Controllers/TasksController.cs:657`
- `GET` `/api/tasks/{taskId}/files` — `API/Controllers/TasksController.cs:670`
- `GET` `/api/tasks/inbox/{userId}` — `API/Controllers/TasksController.cs:687`
- `POST` `/api/tasks/inbox/{messageId}/read` — `API/Controllers/TasksController.cs:697`
- `POST` `/api/tasks/inbox/{userId}/read-all` — `API/Controllers/TasksController.cs:707`
- `POST` `/api/tasks/inbox/{messageId}/reply` — `API/Controllers/TasksController.cs:717`
- `GET` `/api/tasks/inbox/{userId}/unread-count` — `API/Controllers/TasksController.cs:727`
- `GET` `/api/tasks/{taskId}/attachments` — `API/Controllers/TasksController.cs:741`
- `GET` `/api/tasks/attachments/{attachmentId}/image` — `API/Controllers/TasksController.cs:751`
- `GET` `/api/tasks/attachments/{attachmentId}/base64` — `API/Controllers/TasksController.cs:764`
- `POST` `/api/tasks/{taskId}/attachments` — `API/Controllers/TasksController.cs:782`
- `DELETE` `/api/tasks/attachments/{attachmentId}` — `API/Controllers/TasksController.cs:812`
- `POST` `/api/messaging/register` — `API/Controllers/MessagingController.cs:25`
- `POST` `/api/messaging/send` — `API/Controllers/MessagingController.cs:38`
- _...and 134 more_

## External Callers

> Code outside this subsystem that calls into it.

- `MainForm.InitializeMcpServerAndChatPanel` — `MainForm.cs:507`
- `TeamWatcherService.CreateOrphanSubagentPanel` — `Services/TeamWatcherService.cs:1545`
- `TeamWatcherService.CreateTailerForMember` — `Services/TeamWatcherService.cs:1026`
- `MainForm.OnFormClosing` — `MainForm.cs:3580`
- `AgentPanelControl.StopAgentAsync` — `AgentPanel/AgentPanelControl.cs:44`
- `Program.Main` — `AgentProcessTest/Program.cs:91`
- `TeamWatcherService.StopOrphanTailer` — `Services/TeamWatcherService.cs:1778`
- `PlanDatabase.GenerateRecentProgressSection` — `Services/PlanDatabase.cs:802`
- `PlanDatabase.GenerateTaskStackSection` — `Services/PlanDatabase.cs:659`
- `TasksPanelControl.HandleCodeReviewVerdict` — `TasksPanel/TasksPanelControl.cs:991`
- `ActivityPanelDocument.OnTaskCreateRequested` — `ActivityPanel/ActivityPanelDocument.cs:115`
- `TasksPanelControl.OnWebMessageReceived` — `TasksPanel/TasksPanelControl.cs:390`
- `TerminalDocument.UpdateStatusBar` — `Docking/TerminalDocument.cs:2068`
- `MessageBroker.DeleteTask` — `MCPServer/Services/MessageBroker.cs:3235`
- `TaskLifecycleBoardForm.HandleHelpersUpdate` — `TaskLifecycleBoard/TaskLifecycleBoardForm.cs:684`

## Gotchas

- Stale task check runs hourly, office agent cleanup every 5 min, MCP registry seeding on startup from MT's and user's config, CORS enabled for localhost
- Checklist transitions enforce state machine (pending→coding→testing→done), SetTaskActive auto-pauses other tasks for assignee, attachments stored as base64
- SendMessage and Broadcast are async, message delivery may fail silently if terminal disconnects
- KnowledgeDatabase accessed via MessageBroker, must validate query/field parameters, stale detection uses SHA256 hashes
- GET /api/projects/{projectId}/context returns full context object with nested associations
- Path traversal validation required for sessionFilePath and claudeProjectPath, must be within ~/.claude/projects, parent_session_id creates chains
- Requires transcriptPath validation before calling RequestAgentPanelClose
- DebugLogService accessed via MessageBroker.DebugLogService property, must check for null
- CleanupStaleAgents defaults to 30 minutes, ClearAllAgents is nuclear option for ghost cleanup
- Returns processId and sessionId, agent runs headless with piped I/O not ConPTY

---
_Generated 2026-06-10T17:01:21.2373401Z · [Back to index](./index.md)_
