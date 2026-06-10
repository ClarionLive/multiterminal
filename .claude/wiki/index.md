# MultiTerminal Wiki

> Per-subsystem reference articles generated from the code graph + code digests.
> Load this index at session start (~200 tokens) and fetch specific articles on demand.

## Subsystems

- **[MessageBroker](./message-broker.md)** _(core, hub, events)_ — Central event hub routing messages between terminals, caching tasks/terminals/profiles, firing events to UI panels, and delivering webhooks.
- **[TaskDatabase](./task-database.md)** _(core, persistence, sqlite)_ — SQLite persistence for the kanban system. 21+ tables covering tasks, checklists, helpers, inbox, attachments, activity, summaries, and session tracking.
- **[MainForm](./main-form.md)** _(ui, shell, host)_ — WinForms application shell. Hosts docked panels, orchestrates terminal lifecycle, wires MessageBroker events to UI, manages theme and layout.
- **[REST API](./rest-api.md)** _(api, http, integration)_ — HTTP server exposing the REST API on port 5050 across the API/Controllers/ set. Bridges MCP tools and UI panels to MessageBroker and databases.
- **[Code Graph](./code-graph.md)** _(indexing, roslyn, analysis)_ — Roslyn-based C# code indexer and query layer. Extracts symbols (classes, methods, properties) and relationships (calls, inherits, references) into cg_* SQLite tables.
- **[Session Lineage](./session-lineage.md)** _(sessions, history, search)_ — Tracks Claude Code session history, lineage chains (parent/child spawn relationships), and semantic session memory via embeddings + FTS5 search.
- **[Knowledge Database](./knowledge-db.md)** _(knowledge, persistence, search)_ — Stores knowledge entries (patterns, decisions, gotchas), code digests, and research cache. FTS5 search across knowledge content.
- **[MCP Server](./mcp-server.md)** _(mcp, tools, external)_ — Node.js MCP tool definitions that bridge Claude Code agents to MultiTerminal's REST API. Lives outside the repo at %APPDATA%/multiterminal/mcp but managed by McpConfigService.
- **[WebView2 Panel Pattern](./webview2-panels.md)** _(ui, webview2, panel-pattern)_ — Pattern for building UI panels hosted in WebView2. Each panel has an outer DockContent + inner UserControl + HTML/CSS/JS, wired to MessageBroker events.
- **[Terminal Management](./terminal-management.md)** _(terminal, process, conpty)_ — Terminal spawning, ConPty lifecycle, agent process coordination, and companion process management.

_Generated 2026-06-10T17:01:21.3778996Z · 10 articles_
