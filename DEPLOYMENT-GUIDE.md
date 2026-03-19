# MultiTerminal Deployment Guide

**Version:** 1.3.0
**Last Updated:** 2026-03-19

This guide covers how to deploy MultiTerminal to a new machine. The primary method is the Inno Setup installer, which handles everything automatically. A manual deployment section is included for advanced users.

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Installer Deployment (Recommended)](#2-installer-deployment-recommended)
3. [What the Installer Does](#3-what-the-installer-does)
4. [Post-Install Verification](#4-post-install-verification)
5. [Manual Deployment (Advanced)](#5-manual-deployment-advanced)
6. [Component Reference](#6-component-reference)
7. [Troubleshooting](#7-troubleshooting)

---

## 1. Prerequisites

The installer checks for these and warns if missing:

| Requirement | Purpose | Download |
|-------------|---------|----------|
| **Windows 10/11 (x64)** | Host OS | -- |
| **.NET 8 Desktop Runtime + ASP.NET Core** | Application runtime. If not detected, installer bundles all runtime DLLs (self-contained mode). | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **Claude Code CLI** | MCP servers and hooks require `~/.claude.json` to exist. Must be installed and run at least once. | `npm install -g @anthropic-ai/claude-code` |
| **Node.js 18+** | MCP server, hooks, and post-install scripts. | [nodejs.org](https://nodejs.org/) |
| **WebView2 Runtime** | UI panels (pre-installed on Windows 11; may need manual install on Windows 10). | [developer.microsoft.com](https://developer.microsoft.com/microsoft-edge/webview2/) |

---

## 2. Installer Deployment (Recommended)

### Building the Installer

From the source tree, run:

```powershell
cd H:\DevLaptop\ClarionPowerShell\MultiTerminal\installer
.\build-installer.ps1
```

This script:
1. Publishes MultiTerminal (`dotnet publish -c Release -r win-x64 --self-contained true`)
2. Publishes MCP Gateway (`dotnet publish -c Release -r win-x64 --no-self-contained`)
3. Compiles the Inno Setup script (`MultiTerminal.iss`) into `installer\Output\MultiTerminalSetup-1.3.0.exe`

Options:
- `.\build-installer.ps1 -SkipPublish` -- reuse last publish output, compile installer only
- `.\build-installer.ps1 -Verbose` -- show detailed build output

### Running the Installer

1. Run `MultiTerminalSetup-1.3.0.exe`
2. The installer checks prerequisites (.NET 8, Claude Code, Node.js, WebView2) and warns about missing items
3. Choose installation type:
   - **Full** -- app + all Claude Code integration (hooks, skills, agents, MCP servers, docs)
   - **Application only** -- just the app and bundled tools
   - **Custom** -- pick individual components
4. Select optional MCP servers to install alongside MultiTerminal (MSSQL, SQLite, Windows Build Runner, Windows SnapIt, Everything Search)
5. The post-install script configures Claude Code integration automatically
6. Optionally launch MultiTerminal at the end

### Installation Components

| Component | Description | Install Location |
|-----------|-------------|------------------|
| **MultiTerminal Application** (fixed) | Main executable, DLLs, HTML panels | `{app}\` (e.g., `C:\Program Files\MultiTerminal`) |
| **Bundled Tools** (fixed) | ripgrep (`rg.exe`) for code search | `{app}\tools\` |
| **HTML Documentation** | 9-file documentation site | `{app}\docs\html\` |
| **MCP Servers** | `multiterminal` (Node.js) + `mcp-gateway` (.NET) | `%APPDATA%\multiterminal\mcp\` + `{app}\mcp-gateway\` |
| **Session Hooks** (global) | Activity tracking, session management | `%USERPROFILE%\.claude\hooks\` |
| **Session Hooks** (project) | Task routing, inbox, safety, office panel | `{app}\.claude\hooks\` |
| **Skills** | `/kanban-task`, `/project-management`, `/new-project`, `/profile` | `%USERPROFILE%\.claude\skills\` |
| **Specialist Agents** | verifier, debugger, security auditor, devils advocate, test designer, session summarizer, session distiller | `{app}\.claude\agents\` |
| **Optional MCP Servers** | MSSQL, SQLite, Build Runner, SnapIt, Everything Search | `{app}\mcps\{name}\` |

---

## 3. What the Installer Does

### Prerequisite Detection

- **.NET 8:** Checks registry for both `Microsoft.WindowsDesktop.App` and `Microsoft.AspNetCore.App` 8.x. If found, skips ~380 runtime DLLs (framework-dependent mode). If not found, includes them (self-contained mode).
- **Claude Code:** Checks for `~/.claude.json`. Required -- installer aborts if missing.
- **Node.js:** Checks `node --version`. Warns but allows continuing without it.
- **WebView2:** Checks registry keys. Warns but allows continuing without it.

### Post-Install Script (`post-install.js`)

The installer runs `post-install.js` after copying files. It performs 6 steps:

1. **Merge global hooks into `~/.claude/settings.json`** -- adds MultiTerminal hooks to SessionStart, SessionEnd, PreToolUse, PostToolUse, PostToolUseFailure, SubagentStart, SubagentStop. Creates a backup (`.pre-multiterminal.bak`). Removes duplicate hooks on re-install.
2. **Register MCP servers in `~/.claude.json`** -- adds `multiterminal` (Node.js) and `mcp-gateway` (native .NET) server entries. Creates a backup.
3. **Register optional MCP servers** -- writes `gateway-defaults.json` for the MCP Gateway to auto-seed on first startup.
4. **Generate `{app}/.claude/project.json`** -- creates a unique project ID, name, and project-level SessionStart hooks.
5. **Generate `{app}/.claude/settings.local.json`** -- configures all project-level hooks (task routing, inbox checks, safety, subagent office, session status).
6. **Patch `runtimeconfig.json`** -- if .NET 8 was detected, converts self-contained config to framework-dependent for smaller install.

### Uninstall Script (`post-uninstall.js`)

On uninstall, `post-uninstall.js` cleans up:

1. Removes MultiTerminal hooks from `~/.claude/settings.json`
2. Removes global hook script files from `~/.claude/hooks/`
3. Removes skill folders from `~/.claude/skills/`
4. Removes MCP server files from `%APPDATA%\multiterminal\mcp\`
5. Removes MCP server entries from `~/.claude.json`
6. Removes optional MCP servers from gateway database
7. Restores `runtimeconfig.json` backup if it exists
8. Optionally deletes user data (`%APPDATA%\multiterminal\`) -- prompts user

---

## 4. Post-Install Verification

After installation, verify these work:

- [ ] `MultiTerminal.exe` launches without errors
- [ ] Database created at `%APPDATA%\multiterminal\multiterminal.db`
- [ ] WebView2 panels load (Terminal, Tasks, Chat, Activity, etc.)
- [ ] REST API responds: `curl http://localhost:5050/health`
- [ ] MCP server loads in Claude Code (check `claude mcp list`)
- [ ] Hooks execute on Claude Code events (check activity feed in MultiTerminal)
- [ ] Task management works (create task, list tasks)
- [ ] Inter-terminal messaging works (register terminals, send messages)
- [ ] Start Menu shortcuts work (app + documentation)

---

## 5. Manual Deployment (Advanced)

For development or custom setups where the installer is not appropriate.

### Step 1: Build and Copy Application

```powershell
# From MultiTerminal source directory
dotnet publish -c Release -r win-x64 --self-contained true -o bin\Release\net8.0-windows\win-x64\publish

# Copy publish output to target
Copy-Item -Path "bin\Release\net8.0-windows\win-x64\publish\*" `
          -Destination "C:\Applications\MultiTerminal\" -Recurse -Force
```

### Step 2: Deploy MCP Server

The MCP server (Node.js) lives at `%APPDATA%\multiterminal\mcp\`:

```powershell
$mcpDir = "$env:APPDATA\multiterminal\mcp"
New-Item -ItemType Directory -Path $mcpDir -Force
Copy-Item -Path "path\to\mcp-server\*" -Destination $mcpDir -Recurse
```

### Step 3: Register MCP Servers

Edit `%USERPROFILE%\.claude.json` and add under `mcpServers`:

```json
{
  "mcpServers": {
    "multiterminal": {
      "type": "stdio",
      "command": "node",
      "args": ["C:/Users/<USER>/AppData/Roaming/multiterminal/mcp/index.js"],
      "env": {}
    },
    "mcp-gateway": {
      "type": "stdio",
      "command": "C:/Program Files/MultiTerminal/mcp-gateway/McpGateway.exe",
      "args": [],
      "env": {}
    }
  }
}
```

### Step 4: Deploy Hooks

**Global hooks** go to `%USERPROFILE%\.claude\hooks\`:

| Hook File | Event | Purpose |
|-----------|-------|---------|
| `pool-context.js` | SessionStart | Injects plan context and task assignments |
| `activity-hook.js` | PreToolUse, PostToolUse, PostToolUseFailure, SubagentStart, SubagentStop | Records tool usage and agent activity |
| `session-status-hook.js` | SessionStart | Writes active context for session continuity |
| `profile-status-hook.js` | SessionStart, SessionEnd | Updates agent online/offline status |
| `session-import-hook.js` | SessionEnd | Auto-imports session transcripts |

**Project-level hooks** go to `<install-dir>\.claude\hooks\`:

| Hook File | Event | Purpose |
|-----------|-------|---------|
| `task-to-agent-hook.js` | PreToolUse (Task) | Routes task operations to correct agent |
| `inbox-check-hook.js` | PostToolUse, Stop, UserPromptSubmit, SubagentStop | Checks inbox for new messages |
| `subagent-office-hook.js` | SubagentStart, SubagentStop, TeammateIdle | Manages office panel agent avatars |
| `project-context-hook.js` | SessionStart | Injects project context into sessions |
| `safety-hook.js` | PreToolUse (Bash, Read, Write, Edit, SQL) | Guards against dangerous operations |
| `session-status-hook.js` | SessionStart | Writes active context file |
| `active-context-hook.js` | PostToolUse (build, checklist, status) | Auto-writes active context after key events |
| `notification-hook.js` | Various | Forwards runtime notifications |
| `pipeline-trigger-hook.js` | Various | Triggers CI/CD pipeline actions |

Configure hooks in `%USERPROFILE%\.claude\settings.json` -- see `post-install.js` for the exact structure.

### Step 5: Deploy Skills

Copy skill folders to `%USERPROFILE%\.claude\skills\`:

- `kanban-task/` -- Kanban workflow management (`/kanban-task`)
- `project-management/` -- Full project management (`/project-management`)
- `new-project/` -- New project wizard (`/new-project`)
- `multiterminal-addproject/` -- Add existing project (`/add-project`)
- `profile/` -- Agent profile management (`/profile`)

### Step 6: Deploy Specialist Agents

Copy agent definitions to `<install-dir>\.claude\agents\`:

- `verifier.md` -- Confirms work completion
- `debugger.md` -- Root cause analysis
- `security-auditor.md` -- OWASP security scanning
- `code-reviewer.md` -- Code quality review
- `devils-advocate.md` -- Plan scoring and challenges
- `test-designer.md` -- Acceptance criteria generation
- `session-summarizer.md` -- Session recap generation
- `session-distiller.md` -- Session learning compression

### Step 7: Create Data Directory

```powershell
New-Item -ItemType Directory -Path "$env:APPDATA\multiterminal" -Force
```

The application creates `multiterminal.db` automatically on first run.

---

## 6. Component Reference

### Database Files

| Database | Location | Purpose |
|----------|----------|---------|
| `multiterminal.db` | `%APPDATA%\multiterminal\` | Tasks, projects, profiles, activity, session lineage, notifications, agent stats |
| `messages.db` | `%APPDATA%\multiterminal\` | Reliable message delivery queue |
| `plans.db` | `%APPDATA%\multiterminal\` | Plan phases and milestones |
| `gateway.db` | `%APPDATA%\multiterminal\gateway\` | MCP Gateway server registry and tool catalog |

### REST API

The embedded REST API runs on port 5050. See `API/README.md` for full endpoint documentation (21 controllers, 100+ endpoints).

### MCP Servers

| Server | Type | Purpose |
|--------|------|---------|
| **multiterminal** | Node.js (stdio) | Agent tools: tasks, messaging, browser tabs, search, sessions, knowledge, projects |
| **mcp-gateway** | .NET native (stdio) | Aggregates multiple MCP backends (MSSQL, SQLite, Build Runner, SnapIt, Everything Search) |

### HTML Panels

All UI panels use WebView2 with HTML/CSS/JS frontends:

`Terminal/`, `TasksPanel/`, `ChatPanel/`, `ActivityPanel/`, `InboxPanel/`, `ProfilePanel/`, `ProjectPanel/`, `OfficePanel/`, `AgentPanel/`, `StartScreen/`, `TaskLifecycleBoard/`, `Controls/` (status bar)

---

## 7. Troubleshooting

### Installer won't run: "Claude Code was not detected"

Claude Code must be installed and run at least once to create `~/.claude.json`:

```powershell
npm install -g @anthropic-ai/claude-code
claude   # Run once to initialize, then exit
```

### MCP Server not connecting

1. Verify Node.js is installed: `node --version`
2. Check MCP server path in `~/.claude.json`
3. Test manually: `node "%APPDATA%\multiterminal\mcp\index.js"`
4. Check Claude Code logs: `claude mcp list`

### Hooks not executing

1. Verify `~/.claude/settings.json` contains MultiTerminal hook entries
2. Check that hook files exist at the paths referenced in settings
3. Test a hook manually: `node "%USERPROFILE%\.claude\hooks\activity-hook.js"`

### WebView2 panels blank or not loading

1. Install WebView2 Evergreen Runtime from Microsoft
2. Check if `%LOCALAPPDATA%\Microsoft\EdgeWebView` exists
3. Try deleting `%LOCALAPPDATA%\MultiTerminal\EBWebView` (WebView2 cache) and restarting

### Database errors

1. Ensure `%APPDATA%\multiterminal` exists and is writable
2. If database is corrupted, rename the `.db` file -- a fresh one is created on next launch
3. Check disk space

### Build errors when compiling installer

1. Ensure Inno Setup 6.1+ is installed (for `Excludes` support)
2. Verify publish output exists: `.\build-installer.ps1 -SkipPublish` will fail if no prior publish
3. Check that all source directories referenced in `MultiTerminal.iss` exist

### REST API not responding

1. Check that port 5050 is not in use by another application
2. Verify the app started without errors (check the Start Screen for status)
3. Try: `curl http://localhost:5050/health`
