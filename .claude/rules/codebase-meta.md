---
paths:
  - MultiTerminal.csproj
  - "**/*.cs"
---

# Codebase Stats

- ~156 C# production files, ~66K lines of code
- 3 SQLite databases (multiterminal.db for tasks/sessions/knowledge/profiles/code-graph, projects in same db, message queue separate)
- 19 data models, 23 REST controllers, 91 MCP tools
- 11 UI panels (WebView2-based), 11 dialog windows
- REST API on port 5050, MCP server via Node.js, MCP Gateway (McpGateway.exe)

# Keeping This Summary Current

If you made any of these changes, update the relevant rule file before finishing your task:

- Added/removed a **UI panel or dialog** -> Update CLAUDE.md system layers + rules/folder-map.md
- Added/removed a **database table or column** -> Update rules/database-tables.md
- Added/removed a **service, MCP tool, or REST endpoint** -> Update rules/folder-map.md
- Added/removed a **MessageBroker event** -> Update rules/message-broker-events.md
- Changed a **key pattern** (how to add panels, features, tools) -> Update CLAUDE.md
- Significantly changed **LOC of a critical file** (>500 lines) -> Update CLAUDE.md critical files table
