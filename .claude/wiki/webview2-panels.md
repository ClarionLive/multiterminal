# WebView2 Panel Pattern

> Pattern for building UI panels hosted in WebView2. Each panel has an outer DockContent + inner UserControl + HTML/CSS/JS, wired to MessageBroker events.

**Tags:** `ui`, `webview2`, `panel-pattern`

## Key Files

- `TasksPanel/TasksPanelControl.cs` (1286 LOC)
  - WebView2-based UserControl that renders the kanban task board, handling all task CRUD operations, helper management, project filtering, and real-time broker event updates.
- `ActivityPanel/ActivityPanelDocument.cs` (715 LOC)
  - Dockable DockContent host for the Activity Monitor panel that subscribes to MessageBroker, ActivityService, and PoolCoordinator events to display real-time team activity, chat, and task metrics.
- `Controls/HudDashboardPanel/HudDashboardRenderer.cs` (713 LOC)
- `Controls/TaskHudPanel/TaskHudRenderer.cs` (627 LOC)
  - WebView2-based HUD renderer that docks below a terminal, showing the active task checklist for the assigned terminal; auto-shows/hides based on whether an in-progress task with checklist exists.
- `Controls/HudTabContainer/HudTabContainer.cs` (609 LOC)
- `ChatPanel/ChatPanelControl.cs` (499 LOC)
  - WebView2-based UserControl for the multi-agent chat panel that subscribes to MessageBroker events and bridges C# chat messages to a JavaScript UI via JSON messaging.
- `Controls/HudSessionsPanel/HudSessionsRenderer.cs` (348 LOC)
- `Controls/HudNotesPanel/HudNotesRenderer.cs` (337 LOC)
- `Controls/HudKnowledgePanel/HudKnowledgeRenderer.cs` (162 LOC)

## Key Classes

- **HudTabContainer** (class) — `Controls/HudTabContainer/HudTabContainer.cs:15`
- **TabEntry** (class) — `Controls/HudTabContainer/HudTabContainer.cs:484`
- **GraphicsExtensions** (class) — `Controls/HudTabContainer/HudTabContainer.cs:498`
- **TaskHudRenderer** (class) — `Controls/TaskHudPanel/TaskHudRenderer.cs:21`
- **HudDashboardRenderer** (class) — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:24`
- **HudNotesRenderer** (class) — `Controls/HudNotesPanel/HudNotesRenderer.cs:18`
- **HudKnowledgeRenderer** (class) — `Controls/HudKnowledgePanel/HudKnowledgeRenderer.cs:19`
- **HudSessionsRenderer** (class) — `Controls/HudSessionsPanel/HudSessionsRenderer.cs:19`
- **ActivityPanelDocument** (class) — `ActivityPanel/ActivityPanelDocument.cs:16`
- **ChatPanelControl** (class) — `ChatPanel/ChatPanelControl.cs:19`
- **InjectMessageEventArgs** (class) — `ChatPanel/ChatPanelControl.cs:485`
- **ReplyMessageEventArgs** (class) — `ChatPanel/ChatPanelControl.cs:494`
- **TasksPanelControl** (class) — `TasksPanel/TasksPanelControl.cs:21`

## Key Methods

- `HudTabContainer.AddPermanentTab` — `Controls/HudTabContainer/HudTabContainer.cs:128`
- `HudTabContainer.ReorderPermanentTabs` — `Controls/HudTabContainer/HudTabContainer.cs:158`
- `HudTabContainer.GetTabIndex` — `Controls/HudTabContainer/HudTabContainer.cs:188`
- `HudTabContainer.SwitchToTabById` — `Controls/HudTabContainer/HudTabContainer.cs:198`
- `HudTabContainer.AddBrowserTab` — `Controls/HudTabContainer/HudTabContainer.cs:207`
- `HudTabContainer.RemoveBrowserTab` — `Controls/HudTabContainer/HudTabContainer.cs:262`
- `HudTabContainer.SetBrowserContent` — `Controls/HudTabContainer/HudTabContainer.cs:287`
- `HudTabContainer.GetBrowserTab` — `Controls/HudTabContainer/HudTabContainer.cs:312`
- `HudTabContainer.ApplyTheme` — `Controls/HudTabContainer/HudTabContainer.cs:321`
- `HudTabContainer.SetZoomFactor` — `Controls/HudTabContainer/HudTabContainer.cs:347`
- `GraphicsExtensions.FillRoundedRectangle` — `Controls/HudTabContainer/HudTabContainer.cs:500`
- `TaskHudRenderer.SetZoomFactor` — `Controls/TaskHudPanel/TaskHudRenderer.cs:62`
- `TaskHudRenderer.SetDebugLogService` — `Controls/TaskHudPanel/TaskHudRenderer.cs:72`
- `TaskHudRenderer.Initialize` — `Controls/TaskHudPanel/TaskHudRenderer.cs:239`
- `TaskHudRenderer.SetTerminalName` — `Controls/TaskHudPanel/TaskHudRenderer.cs:278`
- `TaskHudRenderer.ApplyTheme` — `Controls/TaskHudPanel/TaskHudRenderer.cs:296`
- `HudDashboardRenderer.Initialize` — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:181`
- `HudDashboardRenderer.SetProject` — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:202`
- `HudDashboardRenderer.SetTerminalName` — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:216`
- `HudDashboardRenderer.ApplyTheme` — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:221`
- `HudDashboardRenderer.SetZoomFactor` — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:228`
- `HudDashboardRenderer.RefreshAll` — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:242`
- `HudNotesRenderer.Initialize` — `Controls/HudNotesPanel/HudNotesRenderer.cs:154`
- `HudNotesRenderer.SetProject` — `Controls/HudNotesPanel/HudNotesRenderer.cs:159`
- `HudNotesRenderer.ApplyTheme` — `Controls/HudNotesPanel/HudNotesRenderer.cs:168`

## External Callers

> Code outside this subsystem that calls into it.

- `TerminalDocument.InitializeComponent` — `Docking/TerminalDocument.cs:394`
- `TerminalDocument.StartTerminal` — `Docking/TerminalDocument.cs:945`
- `MainForm.OnBrowserTabRequested` — `MainForm.cs:733`
- `TerminalDocument.AddBrowserTab` — `Docking/TerminalDocument.cs:1039`
- `TerminalDocument.RemoveBrowserTab` — `Docking/TerminalDocument.cs:1047`
- `TerminalDocument.SetBrowserContent` — `Docking/TerminalDocument.cs:1055`
- `BrowserTabsController.UpdateTab` — `API/Controllers/BrowserTabsController.cs:33`
- `MainForm.HandleCaptureScreenshotAsync` — `MainForm.cs:842`
- `MainForm.HandleExecuteScriptAsync` — `MainForm.cs:784`
- `MainForm.HandleGetConsoleLogsAsync` — `MainForm.cs:803`
- `MainForm.HandleGetElementContentAsync` — `MainForm.cs:823`
- `MainForm.HandleGetMessagesAsync` — `MainForm.cs:887`
- `MainForm.HandlePostMessageAsync` — `MainForm.cs:867`
- `TerminalDocument.GetBrowserTab` — `Docking/TerminalDocument.cs:1063`
- `MainForm.ToggleTheme` — `MainForm.cs:2090`

## Gotchas

- CRITICAL: HudVisibilityRequested must fire before Visible=true to uncollapse Panel2 first — otherwise VisibleChanged never fires in collapsed SplitContainer. SetTerminalName before Initialize stores in _pendingTerminalName. Guard against double-subscribe with -= before +=.
- Periodic refresh timer is DISABLED (commented out) to prevent UI flashing - all updates are event-driven. The timer code is kept as a safety net comment. BeginInvoke used for thread marshalling to avoid blocking.
- Initialize() must be called with a broker before WebView2 loads. Uses both PostWebMessage (string) and PostMessage (JSON) — different methods for different message types. IsPaused state is local to this control instance.
- Task serialization includes Pascal-case fields (Priority, Helpers, ChecklistJson, etc.) alongside camelCase — JS must handle both; open_lifecycle_board message delegates to static TaskLifecycleBoardForm.OpenForTask(); SendMembersToWebView merges profiles + live terminals to avoid gaps in assignee dropdowns

---
_Generated 2026-06-10T17:01:21.3352283Z · [Back to index](./index.md)_
