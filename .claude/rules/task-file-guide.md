---
paths:
  - "**/*.cs"
---

# Task-Specific File Guide

When working on a feature area, read these files in order:

| Working On | Read These Files |
|------------|-----------------|
| **Task/Kanban features** | KanbanTask.cs -> TaskDatabase.cs -> TasksController.cs -> TasksPanelControl.cs |
| **Messaging** | Message.cs -> MessageBroker.cs -> MessagingController.cs -> ChatPanelControl.cs |
| **Activity feed** | ActivityEvent.cs -> ActivityService.cs -> ActivityPanelDocument.cs |
| **Team profiles** | TeamMemberProfile.cs -> TeamController.cs -> ProfilePanelDocument.cs |
| **Notifications/Inbox** | InboxMessage.cs -> NotificationsController.cs -> InboxPanelDocument.cs |
| **Plans/Checklists** | Plan.cs -> PlanDatabase.cs -> TasksController.cs |
| **Helper system** | TaskHelper.cs -> MessageBroker.cs -> OfficeController.cs |
| **Task reports/Pipeline** | TaskReportsController.cs -> TaskDatabase.cs -> TasksPanelControl.cs |
| **Stale task tracking** | StaleTaskService.cs -> TaskDatabase.GetStaleTasks() |
| **REST API** | MultiTerminalRestServer.cs -> Controllers/ (21 controllers) |
| **Terminal spawning** | TerminalSpawner.cs -> SpawnController.cs -> SpawnedTeammate.cs |
| **Terminal streaming** | TerminalStreamService.cs -> TerminalsController.cs (WebSocket, api/terminals/{id}/stream) |
| **Browser tabs (HUD)** | BrowserTabsController.cs -> HudTabContainer/ |
| **Companion processes** | CompanionProcessManager.cs -> CompanionController.cs -> CompanionProcess.cs |
| **MCP Gateway** | GatewayIntegrationService.cs -> GatewayController.cs |
| **Knowledge base** | KnowledgeDatabase.cs -> KnowledgeController.cs |
| **Owner profile** | OwnerProfileService.cs -> OwnerProfileController.cs -> OwnerProfileDialog.xaml |
| **Projects** | Project.cs -> ProjectDatabase.cs -> ProjectContextService.cs -> GET /api/projects/{id}/context |
| **Session lineage** | SessionLineageService.cs -> SessionLineageController.cs -> SessionSyncService.cs |
| **Code graph** | CodeSymbol.cs + CodeRelationship.cs -> CodeGraphDatabase.cs -> CodeGraphQuery.cs -> CSharpCodeGraphIndexer.cs -> CodeGraphController.cs |
