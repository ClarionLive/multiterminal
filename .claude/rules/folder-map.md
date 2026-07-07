---
paths:
  - "**/*.cs"
---

# Folder Map

| Folder | Purpose | Key Files |
|--------|---------|-----------|
| `Services/` | Business logic & SQLite persistence | TaskDatabase, ProjectService, ProjectDatabase, ProjectContextService, KnowledgeDatabase, CodeGraphDatabase, CodeGraphQuery, CSharpCodeGraphIndexer, CodeGraphIndexCoordinator, CodeGraphWatcher, TerminalSpawner, SettingsService, CompanionProcessManager, GatewayIntegrationService, OwnerProfileService, RipgrepService, TerminalStreamService, SessionLineageService, SessionSyncService, TranscriptTailer, GitRepoService, GitRepoManager, GitRepoWatcher, WorktreeManager, StatusLineStatsReader |
| `Services/Startup/` | App-launch resilience (task 4fec40e2) | SingleInstanceGuard (Local\ per-session mutex + existing-window activation), StartupPortContentionClassifier (pure: IsAddressInUse/Classify/BuildMessage), StartupHealthProbe (/api/health self-probe), TcpPortOwnerLookup (GetExtendedTcpTable PID lookup), HealthIdentity (shared /api/health identity marker) |
| `MCPServer/Services/` | MCP server services | **MessageBroker**, PoolCoordinator, ActivityService, StaleTaskService, SpawnService, HttpWebhookService, SessionDiscovery, ComplexityDetector, SummaryService |
| `MCPServer/Models/` | Data models (19 files) | KanbanTask, Plan, Message, InboxMessage, TeamMemberProfile, TaskSummary, ActivityEvent, TaskHelper, ComplexityStats, CodeDigest, KnowledgeEntry, TaskAttachment, CodeSymbol, CodeRelationship |
| `Models/` | App-level models (10 files) | TerminalSessionInfo, SpawnedTeammate, ProjectRegistryEntry, Project, ProjectAssociations, OwnerProfile, CompanionProcess, ClaudeCommand, DebugLogEntry, AgentMessage |
| `API/Controllers/` | REST endpoints (26 controllers) | TasksController, MessagingController, ProjectContextController, TaskReportsController, BrowserTabsController, SessionLineageController, CompanionController, GatewayController, NotificationsController, OwnerProfileController, SpawnController, TeamController, DebugController, AgentStatsController, RipgrepController, TerminalStreamController, XamlPreviewController, OfficeController, KnowledgeController, AgentPanelsController, ToolsController, SessionMemoryController, CodeGraphController, SettingsController, TerminalStatsController (GET /api/terminals/{name}/stats), HealthController (GET /api/health — startup self-probe identity, task 4fec40e2) |
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
