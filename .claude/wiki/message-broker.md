# MessageBroker

> Central event hub routing messages between terminals, caching tasks/terminals/profiles, firing events to UI panels, and delivering webhooks.

**Tags:** `core`, `hub`, `events`

## Key Files

- `MCPServer/Services/MessageBroker.cs` (8428 LOC)
  - Central hub routing messages, managing task/terminal/profile caches, delivering webhooks, firing events to UI panels.
- `MCPServer/Services/ActivityFeedService.cs` (386 LOC)
  - Records manager-level activity events (plan lifecycle, phase transitions, builds) to SQLite with whitelist validation and event broadcast.
- `MCPServer/Services/SummaryService.cs` (362 LOC)
  - Manages task progress summaries with auto-generation on status changes and enhancement workflow for manual refinement.
- `MCPServer/Services/HttpWebhookService.cs` (348 LOC)
  - HTTP listener on port 5000 for agent ready notifications and message delivery webhooks from session start/stop hooks.
- `MCPServer/Services/ComplexityDetector.cs` (264 LOC)
  - Analyzes task title/description for complexity signals (multi-file scope, keywords, unknowns, dependencies, subtasks) to suggest plan creation.
- `MCPServer/Services/StaleTaskService.cs` (167 LOC)
  - Detects and manages stale paused tasks with escalation levels (7-day warning, 14-day urgent) and response tracking.
- `MCPServer/Services/ActivityService.cs` (114 LOC)
  - Tracks real-time terminal activity status (working/idle/blocked) with staleness detection and filtering for online terminals.

## Key Classes

- **MessageBroker** (class) — `MCPServer/Services/MessageBroker.cs:18`
- **BrowserTabEventArgs** (class) — `MCPServer/Services/MessageBroker.cs:5618`
- **InboxUpdatedEventArgs** (class) — `MCPServer/Services/MessageBroker.cs:5647`
- **PlanUpdateEventArgs** (class) — `MCPServer/Services/MessageBroker.cs:5657`
- **TaskClaimedEventArgs** (class) — `MCPServer/Services/MessageBroker.cs:5669`
- **ReportSavedEventArgs** (class) — `MCPServer/Services/MessageBroker.cs:5679`
- **ProjectWithCount** (class) — `MCPServer/Services/MessageBroker.cs:5690`
- **OfficeAgentInfo** (class) — `MCPServer/Services/MessageBroker.cs:5702`
- **OfficeAgentResult** (class) — `MCPServer/Services/MessageBroker.cs:5713`
- **HttpWebhookService** (class) — `MCPServer/Services/HttpWebhookService.cs:15`
- **AgentReadyEventArgs** (class) — `MCPServer/Services/HttpWebhookService.cs:319`
- **MessageDeliveredEventArgs** (class) — `MCPServer/Services/HttpWebhookService.cs:329`
- **ActivityService** (class) — `MCPServer/Services/ActivityService.cs:14`
- **ActivityFeedService** (class) — `MCPServer/Services/ActivityFeedService.cs:13`
- **ActivityFeedEntry** (class) — `MCPServer/Services/ActivityFeedService.cs:327`
- **StaleTaskService** (class) — `MCPServer/Services/StaleTaskService.cs:13`
- **StaleFlagResult** (class) — `MCPServer/Services/StaleTaskService.cs:158`
- **ComplexityDetector** (class) — `MCPServer/Services/ComplexityDetector.cs:12`
- **ComplexityResult** (class) — `MCPServer/Services/ComplexityDetector.cs:241`
- **SummaryService** (class) — `MCPServer/Services/SummaryService.cs:15`

## Key Methods

- `MessageBroker.RequestAgentPanelClose` — `MCPServer/Services/MessageBroker.cs:149`
- `MessageBroker.NotifyReportSaved` — `MCPServer/Services/MessageBroker.cs:164`
- `MessageBroker.RecordNotification` — `MCPServer/Services/MessageBroker.cs:185`
- `MessageBroker.FireSessionLineageUpdated` — `MCPServer/Services/MessageBroker.cs:280`
- `MessageBroker.RegisterTerminal` — `MCPServer/Services/MessageBroker.cs:483`
- `MessageBroker.MarkAgentReady` — `MCPServer/Services/MessageBroker.cs:757`
- `MessageBroker.IsAgentReady` — `MCPServer/Services/MessageBroker.cs:791`
- `MessageBroker.UnregisterTerminal` — `MCPServer/Services/MessageBroker.cs:803`
- `MessageBroker.DisconnectTerminalByName` — `MCPServer/Services/MessageBroker.cs:843`
- `MessageBroker.GetTerminals` — `MCPServer/Services/MessageBroker.cs:865`
- `MessageBroker.SetRemoteMode` — `MCPServer/Services/MessageBroker.cs:890`
- `MessageBroker.GetTerminal` — `MCPServer/Services/MessageBroker.cs:895`
- `MessageBroker.SendMessage` — `MCPServer/Services/MessageBroker.cs:912`
- `MessageBroker.DeliverMessageViaWebhook` — `MCPServer/Services/MessageBroker.cs:1114`
- `MessageBroker.SendReply` — `MCPServer/Services/MessageBroker.cs:1215`
- `MessageBroker.ProcessPendingMessages` — `MCPServer/Services/MessageBroker.cs:1372`
- `MessageBroker.GetPendingMessageCounts` — `MCPServer/Services/MessageBroker.cs:1448`
- `MessageBroker.CleanupOldMessages` — `MCPServer/Services/MessageBroker.cs:1465`
- `MessageBroker.AcknowledgeMessage` — `MCPServer/Services/MessageBroker.cs:1484`
- `MessageBroker.Broadcast` — `MCPServer/Services/MessageBroker.cs:1508`
- `MessageBroker.BroadcastSystemMessage` — `MCPServer/Services/MessageBroker.cs:1571`
- `MessageBroker.NotifyHelperAdded` — `MCPServer/Services/MessageBroker.cs:1628`
- `MessageBroker.NotifyHelpRequested` — `MCPServer/Services/MessageBroker.cs:1754`
- `MessageBroker.NotifyHelpersAdded` — `MCPServer/Services/MessageBroker.cs:1880`
- `MessageBroker.NotifyStaleTask` — `MCPServer/Services/MessageBroker.cs:1902`

## External Callers

> Code outside this subsystem that calls into it.

- `AgentPanelsController.ClosePanel` — `API/Controllers/AgentPanelsController.cs:27`
- `TaskReportsController.SaveReport` — `API/Controllers/TaskReportsController.cs:76`
- `NotificationsController.PostNotification` — `API/Controllers/NotificationsController.cs:67`
- `MainForm.InitializeMcpServerAndChatPanel` — `MainForm.cs:400`
- `MainForm.OnSessionSyncTimerTick` — `MainForm.cs:3943`
- `MainForm.AddNewTerminal` — `MainForm.cs:2229`
- `MainForm.AddNewTerminalAsync` — `MainForm.cs:2740`
- `MainForm.InitializeOracle` — `MainForm.cs:4776`
- `MainForm.OnSpawnAgentRequested` — `MainForm.cs:1177`
- `MainForm.OnStartScreenJustClaude` — `MainForm.cs:2454`
- `MainForm.OnStartScreenNewProject` — `MainForm.cs:2513`
- `MainForm.OnStartScreenOpenPowerShell` — `MainForm.cs:2396`
- `MainForm.OnStartScreenProjectLaunched` — `MainForm.cs:2348`
- `MainForm.PreRegisterTerminal` — `MainForm.cs:2653`
- `MainForm.PreRegisterTerminalWithName` — `MainForm.cs:2676`

## Gotchas

- Large (~4700 LOC) monolithic service, properties for ActivityService/SummaryService/ComplexityDetector/ProjectService/ChangelogService/ActivityFeedService/KnowledgeDb/SessionLineageService/DebugLogService all optional and lazy, task cache updated on every mutation
- Listens on http://localhost:5000/, requires HttpListener prerequisites to be registered, handles both query params and POST body for agent-ready, spawns tasks for each request
- GetTeamActivity only returns online terminals from profiles, GetActivity returns null for stale entries (shows UI as 'Ready to work')
- Event type whitelist approach prevents injection, severity auto-set based on event type, timestamps stored as ISO 8601 strings, IDisposable for connection cleanup
- Day7WarningDays=7, Day14UrgentDays=14, StaleFlagResult tracks new flags per check, GetStaleMessage formats UI messages with unicode indicators
- PlanSuggestionThreshold configurable, scoring weights: MultiFile(20), Complex(15), Unknowns(25), Dependencies(20), Subtasks(20), patterns compiled for performance
- AutoGenerateSummary marks as PendingEnhancement=true (draft state), calls FinalizePendingSummaries before creating new, useFastMode=true extracts context from last 30 min activity

---
_Generated 2026-06-10T17:01:21.1117207Z · [Back to index](./index.md)_
