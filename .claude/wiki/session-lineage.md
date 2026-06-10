# Session Lineage

> Tracks Claude Code session history, lineage chains (parent/child spawn relationships), and semantic session memory via embeddings + FTS5 search.

**Tags:** `sessions`, `history`, `search`

## Key Files

- `Services/SessionLineageService.cs` (1331 LOC)
  - Imports Claude Code JSONL sessions into SQLite with lineage and message extraction
- `Services/SessionMemoryDatabase.cs` (1298 LOC)
- `Services/SessionSyncService.cs` (802 LOC)
  - Incremental sync of Claude Code JSONL sessions from .claude/projects/ folder
- `Services/SessionIndexingService.cs` (751 LOC)
  - Vector embedding indexing service spawning Node.js CLI for semantic search
- `Services/TranscriptTailer.cs` (594 LOC)
  - FileSystemWatcher-based JSONL stream tailer for native agent transcripts
- `API/Controllers/SessionLineageController.cs` (402 LOC)
  - REST endpoints for session lineage: importing transcripts, querying chains, FTS search, and sync from project folders.
- `Services/SessionContextWriter.cs` (248 LOC)
- `API/Controllers/SessionMemoryController.cs` (172 LOC)
- `Services/SessionLineage.cs` (137 LOC)
  - Models for session lineage records and messages with parent/task relationships

## Key Classes

- **ImportSessionResult** (class) — `Services/SessionLineageService.cs:13`
- **SyncResult** (class) — `Services/SessionLineageService.cs:24`
- **SessionLineageService** (class) — `Services/SessionLineageService.cs:38`
- **ParseResult** (struct) — `Services/SessionLineageService.cs:634`
- **SessionLineageRecord** (class) — `Services/SessionLineage.cs:7`
- **SessionMessageRecord** (class) — `Services/SessionLineage.cs:68`
- **SessionIndexingService** (class) — `Services/SessionIndexingService.cs:15`
- **IndexingType** (enum) — `Services/SessionIndexingService.cs:170`
- **IndexResult** (class) — `Services/SessionIndexingService.cs:286`
- **IndexingStats** (class) — `Services/SessionIndexingService.cs:672`
- **IndexingProgressEventArgs** (class) — `Services/SessionIndexingService.cs:698`
- **IndexingCompletedEventArgs** (class) — `Services/SessionIndexingService.cs:719`
- **IndexingErrorEventArgs** (class) — `Services/SessionIndexingService.cs:735`
- **ClaudeSession** (class) — `Services/SessionSyncService.cs:13`
- **ClaudeSessionMessage** (class) — `Services/SessionSyncService.cs:31`
- **ToolUseInfo** (class) — `Services/SessionSyncService.cs:61`
- **SessionSyncService** (class) — `Services/SessionSyncService.cs:72`
- **SessionMemoryDatabase** (class) — `Services/SessionMemoryDatabase.cs:21`
- **TextChunk** (class) — `Services/SessionMemoryDatabase.cs:533`
- **SessionChunkResult** (class) — `Services/SessionMemoryDatabase.cs:1093`

## Key Methods

- `SessionLineageService.ImportSession` — `Services/SessionLineageService.cs:64`
- `SessionLineageService.ImportAllSessionsFromFolder` — `Services/SessionLineageService.cs:116`
- `SessionLineageService.GetClaudeProjectFolder` — `Services/SessionLineageService.cs:165`
- `SessionLineageService.SyncNewSessions` — `Services/SessionLineageService.cs:196`
- `SessionLineageService.GetSessionsByTask` — `Services/SessionLineageService.cs:307`
- `SessionLineageService.GetSessionLineage` — `Services/SessionLineageService.cs:314`
- `SessionLineageService.GetMostRecentSessionForProject` — `Services/SessionLineageService.cs:322`
- `SessionLineageService.UpdateSessionSummary` — `Services/SessionLineageService.cs:335`
- `SessionLineageService.GetUnsummarizedSessions` — `Services/SessionLineageService.cs:342`
- `SessionLineageService.GetRecentSessionMessages` — `Services/SessionLineageService.cs:356`
- `SessionLineageService.GetSessionsByFolder` — `Services/SessionLineageService.cs:363`
- `SessionLineageService.GetSessionMessagesBySessionId` — `Services/SessionLineageService.cs:369`
- `SessionLineageService.SearchSessionMessages` — `Services/SessionLineageService.cs:382`
- `SessionLineageService.GenerateHeuristicSummary` — `Services/SessionLineageService.cs:403`
- `SessionLineageService.GenerateHeuristicSummaryFromMessages` — `Services/SessionLineageService.cs:417`
- `SessionLineageService.BackfillSummaries` — `Services/SessionLineageService.cs:430`
- `SessionLineageService.IsNoisyUserMessagePublic` — `Services/SessionLineageService.cs:460`
- `SessionIndexingService.IndexAllSessionsAsync` — `Services/SessionIndexingService.cs:87`
- `SessionIndexingService.IndexProjectSessionsAsync` — `Services/SessionIndexingService.cs:98`
- `SessionIndexingService.IndexChatMessagesAsync` — `Services/SessionIndexingService.cs:111`
- `SessionIndexingService.GetStatsAsync` — `Services/SessionIndexingService.cs:120`
- `SessionIndexingService.CancelIndexing` — `Services/SessionIndexingService.cs:154`
- `SessionSyncService.GetClaudeProjectFolderName` — `Services/SessionSyncService.cs:103`
- `SessionSyncService.GetClaudeProjectPath` — `Services/SessionSyncService.cs:145`
- `SessionSyncService.ParseSessionFile` — `Services/SessionSyncService.cs:185`

## Routes

- `POST` `/api/session-lineage/import` — `API/Controllers/SessionLineageController.cs:35`
- `GET` `/api/session-lineage/task/{taskId}/sessions` — `API/Controllers/SessionLineageController.cs:81`
- `GET` `/api/session-lineage/{sessionId}/chain` — `API/Controllers/SessionLineageController.cs:96`
- `POST` `/api/session-lineage/sync` — `API/Controllers/SessionLineageController.cs:111`
- `POST` `/api/session-lineage/sync-project` — `API/Controllers/SessionLineageController.cs:150`
- `GET` `/api/session-lineage/latest` — `API/Controllers/SessionLineageController.cs:180`
- `GET` `/api/session-lineage/unsummarized` — `API/Controllers/SessionLineageController.cs:218`
- `PUT` `/api/session-lineage/{sessionId}/summary` — `API/Controllers/SessionLineageController.cs:241`
- `GET` `/api/session-lineage/search` — `API/Controllers/SessionLineageController.cs:262`
- `POST` `/api/session-lineage/backfill-summaries` — `API/Controllers/SessionLineageController.cs:299`
- `POST` `/api/session-lineage/register` — `API/Controllers/SessionLineageController.cs:315`
- `POST` `/api/session-lineage/{sessionId}/ensure-ready` — `API/Controllers/SessionLineageController.cs:345`
- `GET` `/api/session-memory/search` — `API/Controllers/SessionMemoryController.cs:30`
- `POST` `/api/session-memory/index` — `API/Controllers/SessionMemoryController.cs:71`
- `POST` `/api/session-memory/index-project` — `API/Controllers/SessionMemoryController.cs:97`
- `GET` `/api/session-memory/stats` — `API/Controllers/SessionMemoryController.cs:125`
- `GET` `/api/session-memory/unindexed` — `API/Controllers/SessionMemoryController.cs:145`

## External Callers

> Code outside this subsystem that calls into it.

- `HudSessionsRenderer.RefreshSessions` — `Controls/HudSessionsPanel/HudSessionsRenderer.cs:152`
- `MainForm.InitializeMcpServerAndChatPanel` — `MainForm.cs:387`
- `MainForm.OnSessionSyncTimerTick` — `MainForm.cs:3929`
- `MainForm.OnViewSessionRequested` — `MainForm.cs:5507`
- `ProjectPanelDocument.LoadSessionsForProjectAsync` — `Docking/ProjectPanelDocument.cs:977`
- `ProjectPanelDocument.OnSyncSessionsRequested` — `Docking/ProjectPanelDocument.cs:1091`
- `HudDashboardRenderer.RefreshProjectInfo` — `Controls/HudDashboardPanel/HudDashboardRenderer.cs:322`

## Gotchas

- Opens JSONL with FileShare.ReadWrite for live sessions; flattens content arrays with tool_use → '[tool: Name]' stubs
- SessionLineageRecord has parent_session_id for chaining; SessionMessageRecord stores flattened content with tool name stubs
- Timeout set to 2 minutes per batch
- Node.js detection tries both pwsh.exe and powershell.exe
- Stats command returns different field names than sessions command
- Skips hidden files starting with dot
- ProjectPath inferred from JSONL line content, not directory
- Large projects may take time on first sync
- Changed events fire multiple times — needs debouncing
- FileShare.ReadWrite for live session reading

---
_Generated 2026-06-10T17:01:21.2894762Z · [Back to index](./index.md)_
