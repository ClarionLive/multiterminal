# MultiTerminal Deployment Guide

This guide covers all files and configurations needed to deploy the MultiTerminal application to another developer's computer.

## Table of Contents
1. [Main Application Files](#1-main-application-files)
2. [MCP Session History Server](#2-mcp-session-history-server)
3. [Claude Code Hooks](#3-claude-code-hooks)
4. [Project Configuration](#4-project-configuration)
5. [Database and Data Files](#5-database-and-data-files)
6. [Global Claude Configuration](#6-global-claude-configuration)
7. [Installation Instructions](#7-installation-instructions)

---

## 1. Main Application Files

### Current Location (Source)
```
H:\DevLaptop\ClarionPowerShell\Deploy\
```

### Target Location (Destination)
```
<USER_CHOSEN_LOCATION>\MultiTerminal\
```
Example: `C:\Applications\MultiTerminal\`

### Files to Deploy
Copy **all files and folders** from the Deploy directory, including:
- `MultiTerminal.exe` - Main executable
- `MultiTerminal.dll` - Application library
- `MultiTerminal.pdb` - Debug symbols
- `MultiTerminal.deps.json` - Dependency configuration
- `MultiTerminal.runtimeconfig.json` - Runtime configuration
- `MultiTerminal.exe.config` - Application configuration
- `*.dll` - All dependency DLLs (WebView2, SQLite, DockPanelSuite, MCP, etc.)
- `Terminal\` - Terminal HTML panel
- `ProjectPanel\` - Project panel HTML
- `ChatPanel\` - Chat panel HTML
- `ActivityPanel\` - Activity panel HTML
- `TasksPanel\` - Tasks panel HTML
- `runtimes\` - Native runtime libraries
- `x64\` - x64 native libraries
- `win-x64\` - Windows x64 libraries
- `mcp-session-history\` - MCP server (see section 2)

---

## 2. MCP Session History Server

The `mcp-session-history` server provides session history search capabilities.

### Current Location (Source)
```
H:\DevLaptop\ClarionPowerShell\MultiTerminal\mcp-session-history\
```

### Target Location (Destination)
```
<MultiTerminal_Install_Dir>\mcp-session-history\
```

### Files to Deploy
Copy the **entire mcp-session-history folder** including:
- `index.js` - Main MCP server entry point
- `package.json` - NPM package configuration
- `package-lock.json` - Locked dependency versions
- `embedding-service.js` - AI embedding service
- `vector-search.js` - Vector search functionality
- `chunking.js` - Text chunking utilities
- `index-cli.js` - CLI indexing tool
- `node_modules\` - **All dependencies** (critical - includes better-sqlite3, transformers)

**Important:** The `node_modules` folder contains compiled native modules. If copying between different Windows versions or architectures, you may need to run `npm install` in the destination folder.

### Installation Check
After deployment, verify the MCP server works:
```powershell
node <MultiTerminal_Install_Dir>\mcp-session-history\index.js
```

---

## 3. Claude Code Hooks

Hooks integrate Claude Code with MultiTerminal's activity feed and task management.

### Current Location (Source)
```
C:\Users\John Hickey\.claude\hooks\
```

### Target Location (Destination)
```
C:\Users\<NEW_USER>\.claude\hooks\
```

### Files to Deploy

#### 3.1. activity-hook.js
**Purpose:** Records tool usage, build events, and subagent activity to MultiTerminal's activity feed

**Current Path:**
```
C:\Users\John Hickey\.claude\hooks\activity-hook.js
```

**Target Path:**
```
C:\Users\<NEW_USER>\.claude\hooks\activity-hook.js
```

**Configuration Needed:**
- Line 22-23: Update the hard-coded path to point to the new user's mcp-session-history location
- The hook looks for better-sqlite3 in multiple locations; add the new installation path

#### 3.2. pool-context.js
**Purpose:** Injects plan context and Kanban task assignments at Claude Code session start

**Current Path:**
```
C:\Users\John Hickey\.claude\hooks\pool-context.js
```

**Target Path:**
```
C:\Users\<NEW_USER>\.claude\hooks\pool-context.js
```

**Configuration Needed:**
- Line 23-25: Update the hard-coded path to point to the new user's mcp-session-history location
- Line 120: Database path is read from `%APPDATA%\multiterminal\tasks.db` (auto-configured)

---

## 4. Project Configuration

### Current Location (Source)
```
H:\DevLaptop\ClarionPowerShell\MultiTerminal\.claude\
```

### Files to Deploy

#### 4.1. project.json
**Current Path:**
```
H:\DevLaptop\ClarionPowerShell\MultiTerminal\.claude\project.json
```

**Target Path:**
```
<MultiTerminal_Install_Dir>\.claude\project.json
```

**Configuration:**
```json
{
  "id": "<NEW_PROJECT_ID>",
  "name": "MultiTerminal",
  "description": "",
  "changeLog": "",
  "createdAt": "<TIMESTAMP>",
  "lastOpenedAt": "<TIMESTAMP>",
  "isPinned": false,
  "prompts": []
}
```

**Note:** Generate a new project ID (GUID) for the new installation

#### 4.2. settings.local.json (Optional)
**Purpose:** Project-specific permission presets

**Current Path:**
```
H:\DevLaptop\ClarionPowerShell\MultiTerminal\.claude\settings.local.json
```

**Target Path:**
```
<MultiTerminal_Install_Dir>\.claude\settings.local.json
```

Contains pre-approved permissions for MCP tools and common commands.

---

## 5. Database and Data Files

### 5.1. Main Task Database

**Purpose:** Stores tasks, plans, activity feed, chat messages, terminal registrations

**Current Location:**
```
C:\Users\John Hickey\AppData\Roaming\multiterminal\tasks.db
```

**Target Location:**
```
C:\Users\<NEW_USER>\AppData\Roaming\multiterminal\tasks.db
```

**Deployment Options:**
- **Option A:** Don't copy (starts fresh with empty database)
- **Option B:** Copy database to share existing tasks/history
- **Option C:** Export specific data only (e.g., task templates)

**Note:** The application will create a new database automatically on first run if it doesn't exist.

### 5.2. SQLite MCP Database

**Purpose:** Stores MCP server state (if using sqlite MCP server)

**Current Location:**
```
H:\DevLaptop\ClarionPowerShell\MultiTerminal\mcp_sqlite.db
```

**Target Location:**
```
<MultiTerminal_Install_Dir>\mcp_sqlite.db
```

**Note:** This is optional and depends on whether the sqlite MCP server is configured.

### 5.3. Session History Database

**Purpose:** Stores Claude Code session history for the mcp-session-history server

**Current Location:**
```
C:\Users\John Hickey\.claude\sessions.db
```

**Target Location:**
```
C:\Users\<NEW_USER>\.claude\sessions.db
```

**Note:** This is managed by Claude Code itself. Don't copy unless you want to share session history.

---

## 6. Global Claude Configuration

### 6.1. MCP Servers Configuration

**Purpose:** Registers MCP servers with Claude Code

**Current Location:**
```
C:\Users\John Hickey\.claude\mcp_servers.json
```

**Target Location:**
```
C:\Users\<NEW_USER>\.claude\mcp_servers.json
```

**Configuration Example:**
```json
{
  "mcpServers": {
    "mcp-session-history": {
      "command": "node",
      "args": [
        "<MultiTerminal_Install_Dir>\\mcp-session-history\\index.js"
      ],
      "env": {
        "SESSIONS_DB_PATH": "C:\\Users\\<NEW_USER>\\.claude\\sessions.db"
      }
    }
  }
}
```

**Important:** Update all paths to match the new user's installation directories.

### 6.2. Global Claude Instructions

**Purpose:** User-specific instructions for Claude Code behavior

**Current Location:**
```
C:\Users\John Hickey\.claude\CLAUDE.md
```

**Target Location:**
```
C:\Users\<NEW_USER>\.claude\CLAUDE.md
```

**Note:** This is optional and user-specific. Contains PowerShell preferences, multiterminal chat mode instructions, etc.

### 6.3. Memory Files

**Purpose:** Persistent auto-memory for Claude Code

**Current Location:**
```
C:\Users\John Hickey\.claude\projects\H--DevLaptop-ClarionPowerShell-MultiTerminal\memory\
```

**Target Location:**
```
C:\Users\<NEW_USER>\.claude\projects\<PROJECT_PATH_HASH>\memory\
```

**Files:**
- `MEMORY.md` - Main memory file (loaded into system prompt)
- Additional topic-specific memory files

**Note:** Memory files are project-specific. The path hash is auto-generated by Claude Code based on the project path.

---

## 7. Installation Instructions

### Prerequisites
1. Windows 10/11 (x64)
2. .NET 8.0 Runtime (Windows Desktop) - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
3. Node.js 18+ (for MCP servers) - [Download](https://nodejs.org/)
4. Claude Code CLI installed
5. WebView2 Runtime (usually pre-installed on Windows 11)

### Step-by-Step Installation

#### Step 1: Deploy Main Application
```powershell
# Create installation directory
New-Item -ItemType Directory -Path "C:\Applications\MultiTerminal" -Force

# Copy all files from Deploy folder
Copy-Item -Path "H:\DevLaptop\ClarionPowerShell\Deploy\*" `
          -Destination "C:\Applications\MultiTerminal\" `
          -Recurse -Force
```

#### Step 2: Install MCP Session History Dependencies
```powershell
cd "C:\Applications\MultiTerminal\mcp-session-history"
npm install
```

**Note:** If you copied the node_modules folder and it's not working, delete it and run `npm install` fresh.

#### Step 3: Deploy Hooks
```powershell
# Create hooks directory
New-Item -ItemType Directory -Path "$env:USERPROFILE\.claude\hooks" -Force

# Copy hook files
Copy-Item -Path "C:\Users\John Hickey\.claude\hooks\activity-hook.js" `
          -Destination "$env:USERPROFILE\.claude\hooks\activity-hook.js"

Copy-Item -Path "C:\Users\John Hickey\.claude\hooks\pool-context.js" `
          -Destination "$env:USERPROFILE\.claude\hooks\pool-context.js"
```

#### Step 4: Update Hook Paths
Edit both hook files and update hard-coded paths:

**In activity-hook.js (line 22-23):**
```javascript
path.join(__dirname, '..', '..', 'Applications', 'MultiTerminal', 'mcp-session-history', 'node_modules', 'better-sqlite3'),
'C:\\Applications\\MultiTerminal\\mcp-session-history\\node_modules\\better-sqlite3',
```

**In pool-context.js (line 23-25):**
```javascript
path.join(__dirname, '..', '..', 'Applications', 'MultiTerminal', 'mcp-session-history', 'node_modules', 'better-sqlite3'),
'C:\\Applications\\MultiTerminal\\mcp-session-history\\node_modules\\better-sqlite3',
```

#### Step 5: Configure Claude Code Hooks
Create or edit `$env:USERPROFILE\.claude\hooks.json`:

```json
{
  "SessionStart": {
    "command": "node",
    "args": [
      "C:\\Users\\<USER>\\.claude\\hooks\\pool-context.js"
    ],
    "injectAs": "user-prompt-prefix"
  },
  "PreToolUse": {
    "command": "node",
    "args": [
      "C:\\Users\\<USER>\\.claude\\hooks\\activity-hook.js"
    ]
  },
  "PostToolUse": {
    "command": "node",
    "args": [
      "C:\\Users\\<USER>\\.claude\\hooks\\activity-hook.js"
    ]
  },
  "PostToolUseFailure": {
    "command": "node",
    "args": [
      "C:\\Users\\<USER>\\.claude\\hooks\\activity-hook.js"
    ]
  },
  "SubagentStart": {
    "command": "node",
    "args": [
      "C:\\Users\\<USER>\\.claude\\hooks\\activity-hook.js"
    ]
  },
  "SubagentStop": {
    "command": "node",
    "args": [
      "C:\\Users\\<USER>\\.claude\\hooks\\activity-hook.js"
    ]
  }
}
```

#### Step 6: Configure MCP Servers
Create or edit `$env:USERPROFILE\.claude\mcp_servers.json`:

```json
{
  "mcpServers": {
    "mcp-session-history": {
      "command": "node",
      "args": [
        "C:\\Applications\\MultiTerminal\\mcp-session-history\\index.js"
      ],
      "env": {
        "SESSIONS_DB_PATH": "C:\\Users\\<USER>\\.claude\\sessions.db"
      }
    }
  }
}
```

Replace `<USER>` with the actual Windows username.

#### Step 7: Create Application Data Directory
```powershell
New-Item -ItemType Directory -Path "$env:APPDATA\multiterminal" -Force
```

The application will create `tasks.db` automatically on first run.

#### Step 8: Run MultiTerminal
```powershell
cd "C:\Applications\MultiTerminal"
.\MultiTerminal.exe
```

### Verification Checklist

- [ ] MultiTerminal.exe launches without errors
- [ ] Database created at `%APPDATA%\multiterminal\tasks.db`
- [ ] MCP session-history server accessible (check Claude Code logs)
- [ ] Hooks execute on Claude Code events (check activity feed in MultiTerminal)
- [ ] Task management features work (create task, list tasks, etc.)
- [ ] Inter-terminal chat works (register terminals, send messages)
- [ ] WebView2 panels load correctly (Terminal, Chat, Tasks, etc.)

---

## 8. Troubleshooting

### Issue: MCP Server Not Found
**Solution:** Verify Node.js is installed and in PATH. Check mcp_servers.json paths.

### Issue: Hooks Not Executing
**Solution:** Verify hooks.json exists and paths are correct. Check Node.js is in PATH.

### Issue: Database Errors
**Solution:** Ensure %APPDATA%\multiterminal folder exists and is writable.

### Issue: better-sqlite3 Module Not Found
**Solution:** Run `npm install` in the mcp-session-history directory.

### Issue: WebView2 Not Loading
**Solution:** Install WebView2 Runtime from Microsoft.

---

## 9. Optional: Share Configuration

If you want to provide a pre-configured setup, create a deployment package:

```
MultiTerminal-Deploy/
├── Application/                    # Main app files
│   └── (all files from Deploy folder)
├── Hooks/                          # Claude Code hooks
│   ├── activity-hook.js
│   └── pool-context.js
├── Config/                         # Configuration templates
│   ├── hooks.json.template
│   └── mcp_servers.json.template
├── Database/                       # Optional: pre-seeded database
│   └── tasks.db (optional)
└── INSTALLATION.md                 # This guide
```

---

## Summary: Complete File List

### Mandatory Files:
1. ✅ MultiTerminal.exe + all DLLs and dependencies
2. ✅ mcp-session-history folder (entire folder with node_modules)
3. ✅ activity-hook.js (with updated paths)
4. ✅ pool-context.js (with updated paths)
5. ✅ hooks.json (Claude Code hook configuration)
6. ✅ mcp_servers.json (MCP server registration)

### Optional Files:
7. ⭕ tasks.db (start fresh or copy existing)
8. ⭕ sessions.db (Claude Code session history)
9. ⭕ CLAUDE.md (user-specific instructions)
10. ⭕ MEMORY.md (project memory files)
11. ⭕ .claude/settings.local.json (permission presets)
12. ⭕ .claude/project.json (project configuration)

---

## Contact & Support

For questions or issues, refer to the MultiTerminal repository or contact the development team.

**Version:** 1.0
**Last Updated:** 2026-02-05
