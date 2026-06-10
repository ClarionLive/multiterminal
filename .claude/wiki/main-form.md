# MainForm

> WinForms application shell. Hosts docked panels, orchestrates terminal lifecycle, wires MessageBroker events to UI, manages theme and layout.

**Tags:** `ui`, `shell`, `host`

## Key Files

- `MainForm.cs` (6025 LOC)
  - Main application window (3K LOC); hosts 7 docked UI panels (Kanban/Chat/Activity/Profile/Inbox/Project/Launcher), orchestrates terminal lifecycle, wires MessageBroker events to panels.
- `Docking/TerminalDocument.cs` (3505 LOC)
  - Core DockContent wrapper for a terminal control instance, hosting the ConPTY terminal, status bar, task HUD, embedded agent panel, and optional start screen.
- `Program.cs` (134 LOC)
  - Application entry point; checks Windows version for ConPTY support, initializes HiDPI mode, seeding plan DB, and manages splash/main form lifecycle.

## Key Classes

- **MainForm** (class) — `MainForm.cs:33`
- **ProjectTerminalAction** (enum) — `MainForm.cs:5124`
- **ProjectDialogResult** (class) — `MainForm.cs:5131`
- **Program** (class) — `Program.cs:11`
- **OSVERSIONINFOEX** (struct) — `Program.cs:16`
- **TerminalDocument** (class) — `Docking/TerminalDocument.cs:29`
- **SavePromptRequestEventArgs** (class) — `Docking/TerminalDocument.cs:2377`
- **LaunchAsIdentityEventArgs** (class) — `Docking/TerminalDocument.cs:2389`
- **DirectoryChangedEventArgs** (class) — `Docking/TerminalDocument.cs:2401`
- **TaskDroppedEventArgs** (class) — `Docking/TerminalDocument.cs:2413`

## Key Methods

- `MainForm.AddNewTerminal` — `MainForm.cs:2101`
- `MainForm.AddNewTerminalWithIdentity` — `MainForm.cs:2582`
- `MainForm.GetAvailableIdentities` — `MainForm.cs:2595`
- `TerminalDocument.ApplyStatusBarHeight` — `Docking/TerminalDocument.cs:784`
- `TerminalDocument.StartTerminal` — `Docking/TerminalDocument.cs:890`
- `TerminalDocument.SetTheme` — `Docking/TerminalDocument.cs:953`
- `TerminalDocument.FocusTerminal` — `Docking/TerminalDocument.cs:991`
- `TerminalDocument.SetFocusBorder` — `Docking/TerminalDocument.cs:1000`
- `TerminalDocument.SetFontSize` — `Docking/TerminalDocument.cs:1013`
- `TerminalDocument.GetFontSize` — `Docking/TerminalDocument.cs:1021`
- `TerminalDocument.ApplyTaskHudZoom` — `Docking/TerminalDocument.cs:1029`
- `TerminalDocument.AddBrowserTab` — `Docking/TerminalDocument.cs:1037`
- `TerminalDocument.RemoveBrowserTab` — `Docking/TerminalDocument.cs:1045`
- `TerminalDocument.SetBrowserContent` — `Docking/TerminalDocument.cs:1053`
- `TerminalDocument.GetBrowserTab` — `Docking/TerminalDocument.cs:1061`
- `TerminalDocument.ForceCollapseAgentPanel` — `Docking/TerminalDocument.cs:1365`
- `TerminalDocument.ApplyAgentSplitRatio` — `Docking/TerminalDocument.cs:1382`
- `TerminalDocument.ApplyHudSplitRatio` — `Docking/TerminalDocument.cs:1405`
- `TerminalDocument.FreezeLayout` — `Docking/TerminalDocument.cs:1439`
- `TerminalDocument.ToggleHud` — `Docking/TerminalDocument.cs:1449`
- `TerminalDocument.InjectInput` — `Docking/TerminalDocument.cs:1498`
- `TerminalDocument.InjectInputAsync` — `Docking/TerminalDocument.cs:1509`
- `TerminalDocument.SendEnterAsync` — `Docking/TerminalDocument.cs:1522`
- `TerminalDocument.TypeInput` — `Docking/TerminalDocument.cs:1535`
- `TerminalDocument.GetWorkingDirectory` — `Docking/TerminalDocument.cs:1543`

## External Callers

> Code outside this subsystem that calls into it.

- `TestSpawnForm.SpawnButton_Click` — `TestSpawnForm.cs:133`
- `OracleService.Start` — `Services/OracleService.cs:81`
- `ActivityPanelDocument.ApplyTheme` — `ActivityPanel/ActivityPanelDocument.cs:662`
- `OracleTerminalForm.ApplyTheme` — `OracleTerminal/OracleTerminalForm.cs:106`
- `OfficePanelDocument.ApplyTheme` — `OfficePanel/OfficePanelDocument.cs:539`
- `ProfilePanelDocument.SetTheme` — `ProfilePanel/ProfilePanelDocument.cs:99`
- `ProfilePanelRenderer.OnNavigationCompleted` — `ProfilePanel/ProfilePanelRenderer.cs:154`
- `ProjectPanelDocument.SetTheme` — `Docking/ProjectPanelDocument.cs:164`
- `TerminalControl.DoStart` — `Controls/TerminalControl.cs:231`
- `TerminalControl.SetTheme` — `Controls/TerminalControl.cs:926`
- `WebViewTerminalRenderer.OnTerminalReady` — `Terminal/WebViewTerminalRenderer.cs:355`
- `GridLayoutManager.ArrangeAsGrid` — `Docking/GridLayoutManager.cs:169`
- `ActivityPanelDocument.SetFontSize` — `ActivityPanel/ActivityPanelDocument.cs:670`
- `ChatPanelDocument.SetFontSize` — `ChatPanel/ChatPanelDocument.cs:101`
- `OracleTerminalForm.SetFontSize` — `OracleTerminal/OracleTerminalForm.cs:115`

## Gotchas

- Terminal sessions/projects loaded at startup; SplitterMoved events must be deferred to avoid corruption; MessageBroker initialization synchronization critical; TerminalSessionInfo persistence required.
- ConPTY requires Windows 10 build 17763 or later; RtlGetVersion must be called correctly for accurate version info.
- SplitterMoved subscription deferred to avoid ratio corruption during startup; agent panel layout (SplitRight/SplitBelow/TabbedRight/DoNotShow) requires SplitContainer restructuring at runtime; static instanceCount shared across all terminals

---
_Generated 2026-06-10T17:01:21.2035648Z · [Back to index](./index.md)_
