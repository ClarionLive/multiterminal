# MultiTerminal Plugin Architecture

**Date:** 2026-03-29
**Status:** Implemented
**Replaces:** Global hooks + settings-based hook wiring

---

## Overview

All MultiTerminal-specific hooks, skills, agents, and behavioral instructions are packaged as a **Claude Code plugin**. The plugin auto-activates for all MT-spawned terminals via global `enabledPlugins` in `~/.claude/settings.json`. Non-MT sessions (e.g., Clarion IDE addin) get a clean slate with no MT hooks.

---

## Plugin Location

```
~/.claude/plugins/marketplaces/multiterminal-marketplace/plugins/multiterminal/
├── .claude-plugin/
│   └── plugin.json              # Metadata (name, version, author)
├── CLAUDE.md                    # Agent behavioral instructions (kanban, messaging, MCP tools)
├── hooks/
│   ├── hooks.json               # Hook wiring — auto-activates when plugin is enabled
│   ├── activity-hook.js         # Records tool usage to activity feed
│   ├── active-context-hook.js   # Writes ACTIVE-CONTEXT.md on task transitions
│   ├── ask-user-relay-hook.js   # Relays AskUserQuestion to MT UI
│   ├── commentary-hook.js       # Sends events to Commentator agent
│   ├── elicitation-relay-hook.js # Relays elicitations to MT UI
│   ├── inbox-check-hook.js      # Checks inbox on various events
│   ├── notification-hook.js     # Push notifications
│   ├── pipeline-trigger-hook.js # Triggers review pipeline on checklist updates
│   ├── pool-context.js          # Injects kanban/plan context at session start
│   ├── profile-status-hook.js   # Sets team member online/offline status
│   ├── project-context-hook.js  # Injects project context at session start
│   ├── research-cache-hook.js   # Caches web research results
│   ├── safety-hook.js           # PreToolUse safety guard (blocks dangerous commands)
│   ├── session-compact-hook.js  # Saves context on compaction
│   ├── session-import-hook.js   # Imports session transcripts to DB
│   ├── session-save-hook.js     # Saves session on stop/compact
│   ├── session-status-hook.js   # Manages terminal profiles + session agent map
│   ├── stop-relay-hook.js       # Relays stop events
│   ├── subagent-office-hook.js  # Tracks subagent spawn/stop in office panel
│   └── task-to-agent-hook.js    # Routes tasks to specialized agents
├── skills/                      # 11 skills (kanban-task, pipeline, session-start, etc.)
│   ├── kanban-task/
│   ├── pipeline/
│   ├── project-management/
│   ├── session-start/
│   ├── daily-digest/
│   ├── new-project/
│   ├── multiterminal-addproject/
│   ├── reload-context/
│   ├── start-servers/
│   ├── verifier-multiterminal/
│   └── profile/
└── agents/                      # 9 specialist agents + report template
    ├── code-reviewer.md
    ├── debugger.md
    ├── devils-advocate.md
    ├── security-auditor.md
    ├── session-distiller.md
    ├── session-reviewer.md
    ├── session-summarizer.md
    ├── test-designer.md
    ├── verifier.md
    └── report-template.html
```

---

## How It Works

### Plugin Discovery & Activation

1. The marketplace directory lives at `~/.claude/plugins/marketplaces/multiterminal-marketplace/`
2. Global `~/.claude/settings.json` declares the marketplace and enables the plugin:
   ```json
   {
     "extraKnownMarketplaces": {
       "multiterminal-marketplace": {
         "source": { "source": "directory", "path": "..." }
       }
     },
     "enabledPlugins": {
       "multiterminal@multiterminal-marketplace": true
     }
   }
   ```
3. When Claude Code starts, it discovers the marketplace, loads the plugin, and:
   - Merges `hooks/hooks.json` into the active hook configuration
   - Makes skills available via `/skill-name`
   - Makes agents available for subagent spawning
   - Loads `CLAUDE.md` as additional context

### Hook Wiring

Hooks are declared in `hooks/hooks.json` using `${CLAUDE_PLUGIN_ROOT}` for portable paths:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "node \"${CLAUDE_PLUGIN_ROOT}/hooks/safety-hook.js\"",
            "timeout": 5
          }
        ]
      }
    ]
  }
}
```

This replaces the old approach of wiring hooks in `settings.local.json` with hardcoded absolute paths.

### CLAUDE.md Split

| File | Loaded when | Content |
|------|-------------|---------|
| Plugin `CLAUDE.md` | Any MT-spawned terminal (any project) | Agent behavior: kanban workflow, messaging, MCP tools, task terminology |
| Project `.claude/CLAUDE.md` | Working in MT source directory only | Codebase reference: architecture, DB tables, folder map, patterns |

---

## Terminal Launch Flow

With the plugin handling hooks and CLAUDE.md, `LaunchCommandBuilder.cs` is simplified:

**Before (3 flags):**
```
claude --add-dir <mt-source> --settings <settings.local.json> --mcp-config <mcp.json> --dangerously-skip-permissions --dangerously-load-development-channels server:multiterminal-channel
```

**After (1 flag):**
```
claude --mcp-config <mcp.json> --dangerously-skip-permissions --dangerously-load-development-channels server:multiterminal-channel
```

- `--add-dir` removed: Plugin provides CLAUDE.md; project has its own
- `--settings` removed: Plugin provides hooks via hooks.json; no settings-based hook wiring needed
- `--mcp-config` kept: Centralized MCP server registration at `%APPDATA%\multiterminal\.mcp.json`

---

## Configuration Files

| File | Scope | Contains |
|------|-------|---------|
| `~/.claude/settings.json` | Global (all sessions) | `enabledPlugins`, `extraKnownMarketplaces`, permissions, prefs |
| `MultiTerminal/.claude/settings.json` | MT project only | Empty `{}` (everything moved to plugin or global) |
| `MultiTerminal/.claude/settings.local.json` | MT project only | Permissions for MT development, prefs |
| Plugin `hooks/hooks.json` | When plugin is active | All 20 hooks across 13 lifecycle events |

---

## Adding a New Plugin

To create a plugin for another product (e.g., Clarion Assistant):

1. Create the plugin directory:
   ```
   ~/.claude/plugins/marketplaces/multiterminal-marketplace/plugins/clarion-assistant/
   ├── .claude-plugin/plugin.json
   ├── CLAUDE.md
   ├── hooks/hooks.json
   ├── skills/
   └── agents/
   ```

2. Enable it globally (or per-project):
   ```json
   "enabledPlugins": {
     "multiterminal@multiterminal-marketplace": true,
     "clarion-assistant@multiterminal-marketplace": true
   }
   ```

3. Each plugin's hooks, skills, agents, and CLAUDE.md load independently and don't interfere with each other.

---

## Migration History

| Date | Change |
|------|--------|
| 2026-03-29 | Initial plugin creation: 15 hooks, 11 skills, 9 agents migrated from global/project locations |
| 2026-03-29 | Moved 5 remaining global hooks into plugin (activity, commentary, pool-context, session-import, profile-status) |
| 2026-03-29 | Created `hooks/hooks.json` — all hook wiring now in plugin, removed from `settings.local.json` and global `settings.json` |
| 2026-03-29 | Split CLAUDE.md — agent behavior in plugin, codebase reference in project |
| 2026-03-29 | Simplified `LaunchCommandBuilder.cs` — removed `--add-dir` and `--settings` flags |
| 2026-03-29 | Removed `CLARION_ASSISTANT_EMBEDDED` guards from all hooks (plugin scoping makes them unnecessary) |
