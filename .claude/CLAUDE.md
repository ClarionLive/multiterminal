# MultiTerminal Codebase Reference

A multi-agent coordination system for Claude Code. WinForms desktop app (C#/.NET) with integrated REST API (port 5050), MCP server, and WebView2-based UI panels.

> Agent behavioral instructions (kanban workflow, MCP tools, messaging, task terminology) are in the MultiTerminal plugin CLAUDE.md. This file is only the codebase reference for working on MT source code.
> Detailed reference tables (database schema, folder map, task guides, events) are in `.claude/rules/` and load on demand.

## Architecture

```
MainForm.cs (5.2K LOC) - UI Host, 11 WebView2 panels
  REST API (port 5050) - 23 controllers in API/Controllers/
  MessageBroker.cs (5.5K LOC) - Central hub, routes messages, fires events
  SQLite (TaskDatabase.cs 4.6K LOC) - 21+ tables: tasks, sessions, knowledge, profiles
  CodeGraph (Roslyn) - CodeGraphDatabase + CSharpCodeGraphIndexer, cg_ tables in same SQLite
  MCP Server (Node.js) - 88 tools at %APPDATA%/multiterminal/mcp
```

## Critical Files

| File | LOC | Role |
|------|-----|------|
| `MCPServer/Services/MessageBroker.cs` | 5,512 | Central hub. Routes messages, caches tasks/terminals/profiles, fires events. |
| `Services/TaskDatabase.cs` | 4,621 | SQLite CRUD for 21+ tables. |
| `MainForm.cs` | 5,162 | UI host. Creates/docks panels, wires events. |
| `MCPServer/Models/KanbanTask.cs` | 476 | Core task model with status, checklist, plan, continuation notes. |
| `Services/CSharpCodeGraphIndexer.cs` | 380 | Roslyn-based 2-pass C# code indexer (symbols + relationships). |
| `Services/CodeGraphDatabase.cs` | 250 | SQLite CRUD for code graph (cg_symbols, cg_relationships, cg_projects). |

## Key Patterns

**Adding a UI Panel:**
1. Create `{Name}Panel/{Name}PanelDocument.cs` inheriting `DockContent` (HideOnClose=true)
2. Create inner control with WebView2 or custom renderer
3. Add `Initialize(MessageBroker broker)` and `ApplyTheme(bool isDark)` methods
4. In MainForm: instantiate, Initialize(), wire events, add toolbar toggle button

**Adding a Backend Feature:**
1. Add model to `MCPServer/Models/`
2. Add persistence to `TaskDatabase.cs` (table + CRUD + migration)
3. Add routing to `MessageBroker.cs` (methods + events)
4. Add MCP tool to Node.js server if agents need access
5. Add REST endpoint to `API/Controllers/` if HTTP needed

**Adding an MCP Tool:**
1. Add tool definition in `%APPDATA%/multiterminal/mcp/index.js`
2. Tools call REST API at localhost:5050 -> MessageBroker -> TaskDatabase
3. Return formatted result with success/error context

## Compaction Instructions

When compacting, always preserve: active task ID and title, list of modified files, current checklist state, any test commands or build commands discussed, and continuation notes for session handoff.
