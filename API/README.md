# MultiTerminal REST API Documentation

**Base URL:** `http://localhost:5050`
**Version:** 1.3.0

## Table of Contents

1. [Health Check](#health-check)
2. [Tools (Self-Documenting)](#tools-api)
3. [Messaging](#messaging-api)
4. [Tasks](#tasks-api)
5. [Task Checklist](#task-checklist-api)
6. [Task Relationships](#task-relationships-api)
7. [Task File Links](#task-file-links-api)
8. [Task Code Review](#task-code-review-api)
9. [Task Attachments](#task-attachments-api)
10. [Task Reports](#task-reports-api)
11. [Inbox](#inbox-api)
12. [Projects](#projects-api)
13. [Session Lineage](#session-lineage-api)
14. [Knowledge & Code Digests](#knowledge-api)
15. [Agent Stats & Invocations](#agent-stats-api)
16. [Browser Tabs (HUD)](#browser-tabs-api)
17. [Office Panel](#office-panel-api)
18. [Spawn](#spawn-api)
19. [Team](#team-api)
20. [Debug](#debug-api)
21. [Search (Ripgrep)](#search-api)
22. [Notifications](#notifications-api)
23. [Owner Profile](#owner-profile-api)
24. [Gateway (MCP)](#gateway-api)
25. [Companions](#companions-api)
26. [Terminal Stream (WebSocket)](#terminal-stream-api)
27. [XAML Preview](#xaml-preview-api)
28. [Agent Panels](#agent-panels-api)

---

## Health Check

```
GET /
GET /health
```

Returns health status and port info.

---

## Tools API

**Route:** `/api`

### List All Endpoints
```
GET /api/tools
```
Returns a self-documenting list of all API endpoints with descriptions.

---

## Messaging API

**Route:** `/api/messaging`

### Register Terminal
```
POST /api/messaging/register
```
**Body:**
```json
{
  "name": "Alice",
  "docId": "abc123"
}
```
**Response:** `{ "terminalId": "xyz789" }`

### Send Message
```
POST /api/messaging/send
```
**Body:**
```json
{
  "fromTerminalId": "xyz789",
  "to": "Bob",
  "message": "Hello!",
  "priority": "normal"
}
```
`priority` is optional. Values: `"low"`, `"normal"` (default), `"high"`, `"critical"`.

### Broadcast Message
```
POST /api/messaging/broadcast
```
**Body:**
```json
{
  "fromTerminalId": "xyz789",
  "message": "Hello everyone!"
}
```

### Get Messages
```
GET /api/messaging/messages/{terminalId}
```
Returns pending messages for the given terminal.

### List Terminals
```
GET /api/messaging/terminals
```
Returns all registered terminals with connection status.

### Disconnect Terminal
```
POST /api/messaging/disconnect
```
**Body:**
```json
{
  "name": "Alice"
}
```
Disconnects a terminal by name. Used by session-end hooks.

---

## Tasks API

**Route:** `/api/tasks`

### List Tasks
```
GET /api/tasks?status=all
```
Query param `status`: `all`, `todo`, `in_progress`, `done`, `suggestion`.

### Get Task
```
GET /api/tasks/{taskId}
```

### Get Task Detail
```
GET /api/tasks/{taskId}/detail
```
Returns the full task with parsed checklist, checklist summary, relationships, and file links.

### Get Active Task for Agent
```
GET /api/tasks/active/{agentName}
```
Returns the in-progress task for a specific agent, with checklist summary.

### Get Pickable Tasks for Agent
```
GET /api/tasks/pickable/{agentName}
```
Returns tasks relevant to an agent: their assigned tasks + unassigned todo tasks. Compact summary format (no full descriptions, plans, or checklist JSON).

### Create Task
```
POST /api/tasks
```
**Body:**
```json
{
  "title": "Fix bug",
  "description": "Details here",
  "createdBy": "Alice",
  "status": "todo",
  "priority": "normal",
  "projectId": "optional-project-id"
}
```
**Response:** `{ "taskId": "abc123", "task": { ... } }`

### Update Task Status
```
PATCH /api/tasks/{taskId}/status
```
**Body:**
```json
{
  "status": "in_progress",
  "updatedBy": "Alice"
}
```

### Delete Task
```
DELETE /api/tasks/{taskId}?deletedBy=Alice
```

### Assign/Claim Task
```
POST /api/tasks/{taskId}/assign
```
**Body:**
```json
{
  "assignee": "Alice"
}
```

### Set Task Active
```
POST /api/tasks/{taskId}/activate
```
**Body:**
```json
{
  "updatedBy": "Alice"
}
```
Sets a task as active, auto-pausing other active tasks for the same assignee.
**Response:** `{ "success": true, "pausedTaskIds": [...], "pausedTaskTitles": [...] }`

### Update Continuation Notes
```
PATCH /api/tasks/{taskId}/continuation
```
**Body:**
```json
{
  "continuationNotes": "Session handoff context...",
  "updatedBy": "Alice"
}
```

### Update Task Plan
```
PATCH /api/tasks/{taskId}/plan
```
**Body:**
```json
{
  "plan": "Markdown plan content...",
  "updatedBy": "Alice"
}
```

### Update Task Summary
```
PATCH /api/tasks/{taskId}/summary
```
**Body:**
```json
{
  "implementationSummary": "What was built...",
  "testResults": "Test output...",
  "updatedBy": "Alice"
}
```

### Add Helper
```
POST /api/tasks/{taskId}/helpers
```
**Body:**
```json
{
  "helper": "Bob",
  "addedBy": "Alice"
}
```
**Response:** `{ "success": true, "helperCount": 2 }`

### Remove Helper
```
DELETE /api/tasks/{taskId}/helpers/{helperName}
```
**Response:** `{ "success": true, "helperCount": 1 }`

---

## Task Checklist API

**Route:** `/api/tasks/{taskId}/checklist`

### Replace Checklist
```
PATCH /api/tasks/{taskId}/checklist
```
**Body:**
```json
{
  "checklistJson": "[{\"item\":\"Add validation\",\"status\":\"pending\"}]"
}
```

### Transition Checklist Item
```
POST /api/tasks/{taskId}/checklist/{itemIndex}/transition
```
**Body:**
```json
{
  "newStatus": "coding",
  "notes": "Starting implementation",
  "updatedBy": "Alice"
}
```
State machine enforced. Returns cycle count and escalation info.

### Assign Checklist Item
```
POST /api/tasks/{taskId}/checklist/{itemIndex}/assign
```
**Body:**
```json
{
  "assignee": "Bob"
}
```

---

## Task Relationships API

**Route:** `/api/tasks/{taskId}/relationships`

### Add Relationship
```
POST /api/tasks/{taskId}/relationships
```
**Body:**
```json
{
  "targetTaskId": "other-task-id",
  "type": "blocks",
  "createdBy": "Alice"
}
```

### Get Relationships
```
GET /api/tasks/{taskId}/relationships
```

### Remove Relationship
```
DELETE /api/tasks/{taskId}/relationships/{relatedTaskId}
```
Removes both directions of the relationship.

---

## Task File Links API

**Route:** `/api/tasks/{taskId}/files`

### Link File
```
POST /api/tasks/{taskId}/files
```
**Body:**
```json
{
  "filePath": "H:\\path\\to\\file.cs",
  "description": "Main service file",
  "lineStart": 10,
  "lineEnd": 50,
  "addedBy": "Alice"
}
```

### Get Linked Files
```
GET /api/tasks/{taskId}/files
```

### Unlink File
```
POST /api/tasks/{taskId}/files/unlink
```
**Body:**
```json
{
  "filePath": "H:\\path\\to\\file.cs"
}
```

---

## Task Code Review API

**Route:** `/api/tasks/{taskId}/code-review`

### Get Code Review Diffs
```
GET /api/tasks/{taskId}/code-review
```
Returns git diff output for all linked files in a task.

---

## Task Attachments API

**Route:** `/api/tasks/{taskId}/attachments`

### Get Attachments
```
GET /api/tasks/{taskId}/attachments?itemIndex=2
```
`itemIndex` is optional; filters by checklist item.

### Add Attachment
```
POST /api/tasks/{taskId}/attachments
```
**Body:**
```json
{
  "checklistItemIndex": 2,
  "fileName": "screenshot.png",
  "mimeType": "image/png",
  "base64Data": "<base64-encoded-image>",
  "addedBy": "Alice"
}
```
**Response:** `{ "attachmentId": "att123" }`

### Get Attachment Image (binary)
```
GET /api/tasks/attachments/{attachmentId}/image
```
Returns raw binary image data with correct MIME type.

### Get Attachment as Base64
```
GET /api/tasks/attachments/{attachmentId}/base64
```
**Response:** `{ "base64": "...", "mimeType": "image/png", "fileName": "screenshot.png" }`

### Delete Attachment
```
DELETE /api/tasks/attachments/{attachmentId}
```

---

## Task Reports API

**Route:** `/api/tasks/{taskId}/reports`

### List Reports
```
GET /api/tasks/{taskId}/reports?agentName=Verifier&limit=50
```
Returns report metadata (no content) for a task. Optional agent name filter.

### Get Full Report
```
GET /api/tasks/{taskId}/reports/{reportId}
```
Returns full report content (HTML/markdown).

### Save Report
```
POST /api/tasks/{taskId}/reports
```
**Body:**
```json
{
  "id": "optional-id",
  "agentName": "Verifier",
  "invocationId": "inv123",
  "reportType": "html",
  "reportContent": "<h1>Report</h1>...",
  "verdict": "pass",
  "score": 95,
  "createdBy": "Alice"
}
```

---

## Inbox API

**Route:** `/api/tasks/inbox`

### Get Inbox Messages
```
GET /api/tasks/inbox/{userId}?unreadOnly=false&limit=50
```
**Response:**
```json
{
  "success": true,
  "messages": [
    {
      "id": "abc12345",
      "userId": "john",
      "taskId": "xyz789",
      "taskTitle": "Add login feature",
      "checklistItemIndex": 2,
      "checklistItemName": "Add validation",
      "type": "ready_for_testing",
      "summary": "Diana finished 'Add validation' - ready for testing",
      "createdAt": "2026-02-10T10:30:00Z",
      "createdBy": "Diana",
      "readAt": null,
      "replyText": null,
      "repliedAt": null
    }
  ],
  "unreadCount": 5,
  "totalCount": 1
}
```
Message types: `ready_for_testing`, `escalation`, `task_complete`, `helper_request`.

### Mark Message as Read
```
POST /api/tasks/inbox/{messageId}/read
```

### Mark All Messages as Read
```
POST /api/tasks/inbox/{userId}/read-all
```

### Reply to Inbox Message
```
POST /api/tasks/inbox/{messageId}/reply
```
**Body:**
```json
{
  "replyText": "Fixed the issue, ready for re-testing"
}
```
Auto-marks message as read.

### Get Unread Count
```
GET /api/tasks/inbox/{userId}/unread-count
```
**Response:** `{ "count": 5 }`

---

## Projects API

**Route:** `/api/projects`

### List Projects
```
GET /api/projects
```
Returns all projects with rich model (associations included).

### Get Project
```
GET /api/projects/{projectId}
```

### Update Project Fields
```
PATCH /api/projects/{projectId}
```
**Body:**
```json
{
  "fields": {
    "name": "New Name",
    "deploy_path": "C:\\Apps\\Deploy"
  }
}
```
**Response:** `{ "updated": ["name", "deploy_path"], "rejected": [] }`

### Get Project Context
```
GET /api/projects/{projectId}/context
```
Returns the full project context with all associations (agents, MCP servers, specialists, paths, prompts, skills). Designed for agent consumption.

### Get All Project Contexts
```
GET /api/projects/contexts
```

### Add Agent to Project
```
POST /api/projects/{projectId}/agents
```
**Body:**
```json
{
  "agentName": "Alice",
  "role": "developer",
  "preferredModel": "opus"
}
```

### Remove Agent from Project
```
DELETE /api/projects/{projectId}/agents/{agentName}
```

### Add MCP Server to Project
```
POST /api/projects/{projectId}/mcp-servers
```
**Body:**
```json
{
  "serverName": "sqlite",
  "isEnabled": true
}
```

### Remove MCP Server from Project
```
DELETE /api/projects/{projectId}/mcp-servers/{serverName}
```

### Add Specialist Agent
```
POST /api/projects/{projectId}/specialists
```
**Body:**
```json
{
  "agentType": "verifier",
  "isEnabled": true,
  "customPrompt": "Optional custom instructions"
}
```

### Remove Specialist Agent
```
DELETE /api/projects/{projectId}/specialists/{agentType}
```

### Add Path
```
POST /api/projects/{projectId}/paths
```
**Body:**
```json
{
  "pathType": "source",
  "pathValue": "H:\\Dev\\MyProject",
  "description": "Main source directory"
}
```

### Remove Path
```
DELETE /api/projects/{projectId}/paths/{pathId}
```

### Add Prompt
```
POST /api/projects/{projectId}/prompts
```
**Body:**
```json
{
  "promptType": "system",
  "promptText": "Always use strict typing",
  "displayOrder": 0
}
```

### Remove Prompt
```
DELETE /api/projects/{projectId}/prompts/{promptId}
```

### Add Skill
```
POST /api/projects/{projectId}/skills
```
**Body:**
```json
{
  "skillName": "kanban-task",
  "isEnabled": true
}
```

### Remove Skill
```
DELETE /api/projects/{projectId}/skills/{skillName}
```

---

## Session Lineage API

**Route:** `/api/session-lineage`

### Import Session
```
POST /api/session-lineage/import
```
**Body:**
```json
{
  "sessionFilePath": "C:\\Users\\...\\session.jsonl",
  "taskId": "task123",
  "agentName": "Alice",
  "sessionType": "coding",
  "parentSessionId": "parent-guid"
}
```
Imports a Claude Code session JSONL file and links it to a kanban task. Path must be within `~/.claude/projects/`.

### Get Sessions by Task
```
GET /api/session-lineage/task/{taskId}/sessions
```
Returns all session lineage records for a task, newest first.

### Get Session Chain
```
GET /api/session-lineage/{sessionId}/chain
```
Returns the full lineage chain for a session (root to leaf).

### Sync Sessions
```
POST /api/session-lineage/sync
```
**Body:**
```json
{
  "claudeProjectPath": "C:\\Users\\...\\projects\\project-hash",
  "agentName": "Alice",
  "taskId": "task123"
}
```
Incrementally syncs sessions from a Claude project folder.

### Get Latest Session
```
GET /api/session-lineage/latest?projectPath=...&agentName=Alice
```
Returns the most recent session for a project folder. Includes recent messages if no cached summary exists.

### Get Unsummarized Sessions
```
GET /api/session-lineage/unsummarized?projectPath=...&limit=10
```
Returns sessions without cached summaries for batch summary generation.

### Update Session Summary
```
PUT /api/session-lineage/{sessionId}/summary
```
**Body:**
```json
{
  "summary": "This session implemented feature X..."
}
```

### Search Session History
```
GET /api/session-lineage/search?taskId=...&query=...&role=assistant&agentName=Alice&limit=50
```
Full-text search across session messages. At least one of `taskId` or `query` is required.

---

## Knowledge API

**Route:** `/api/knowledge`

### Search Knowledge
```
GET /api/knowledge/search?query=injection&category=security&projectId=proj1&tags=fix&limit=20
```
All query params optional (at least one recommended).

### Add Knowledge Entry
```
POST /api/knowledge
```
**Body:** KnowledgeEntry object with `title` (required), `content` (required), plus optional `category`, `tags`, `confidence`, `projectId`, `sourceAgent`.

### Update Knowledge Entry
```
PUT /api/knowledge/{id}
```
**Body:** Dictionary of field names to values. Accepted keys: `category`, `title`, `content`, `tags`, `confidence`, `superseded_by`.

### Get Code Digest
```
GET /api/knowledge/digest?filePath=Services/TaskDatabase.cs&projectId=proj1
```

### Save Code Digest
```
POST /api/knowledge/digest
```
**Body:** CodeDigest object with `filePath` (required), plus optional `projectId`, `summary`, `publicApi`, `dependencies`, `fileHash`.

### Get Stale Digests
```
POST /api/knowledge/digest/stale?projectId=proj1
```
**Body:**
```json
{
  "fileHashes": {
    "Services/TaskDatabase.cs": "sha256hash...",
    "Services/ProjectService.cs": "sha256hash..."
  }
}
```
Returns digests whose file hash has changed.

---

## Agent Stats API

**Route:** `/api/agents`

### Get Aggregated Stats
```
GET /api/agents/stats
```
Returns performance stats per agent (invocation counts, scores, etc.).

### List Invocations
```
GET /api/agents/invocations?agentName=Verifier&taskId=task123&limit=50
```
All query params optional.

### Record Invocation
```
POST /api/agents/invocations
```
**Body:**
```json
{
  "id": "optional-id",
  "agentName": "Verifier",
  "taskId": "task123",
  "invokedBy": "Alice",
  "modelUsed": "opus",
  "verdict": "pass",
  "score": 95,
  "findingsCount": 3,
  "durationMs": 12000,
  "invokedAt": "2026-03-19T10:00:00Z",
  "completedAt": "2026-03-19T10:00:12Z",
  "reportSummary": "All checks passed"
}
```

---

## Browser Tabs API

**Route:** `/api/browser-tabs`

Per-terminal tabbed WebView2 HUD area. Agents can open HTML content, URLs, run scripts, and interact with browser tabs.

### Open Tab
```
POST /api/browser-tabs/open
```
**Body:**
```json
{
  "terminalId": "term1",
  "title": "My Dashboard",
  "url": "https://example.com",
  "content": "<h1>Or raw HTML</h1>"
}
```
Provide either `url` or `content`. Returns `{ "success": true, "tabId": "tab123" }`.

### Update Tab
```
POST /api/browser-tabs/update
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123",
  "title": "Updated Title",
  "url": "https://new-url.com",
  "content": "<h1>Or new HTML</h1>"
}
```

### Close Tab
```
POST /api/browser-tabs/close
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123"
}
```

### Execute Script
```
POST /api/browser-tabs/execute-script
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123",
  "script": "document.title"
}
```
**Response:** `{ "success": true, "result": "Page Title" }`

### Get Console Logs
```
POST /api/browser-tabs/console-logs
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123",
  "limit": 50
}
```

### Get Element Content
```
POST /api/browser-tabs/element-content
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123",
  "selector": "#myElement",
  "property": "innerHTML"
}
```

### Capture Screenshot
```
POST /api/browser-tabs/capture-screenshot
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123"
}
```
**Response:** `{ "success": true, "imageBase64": "..." }`

### Post Message to Tab
```
POST /api/browser-tabs/post-message
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123",
  "data": "{\"action\":\"refresh\"}"
}
```

### Get Messages from Tab
```
POST /api/browser-tabs/get-messages
```
**Body:**
```json
{
  "terminalId": "term1",
  "tabId": "tab123",
  "limit": 50
}
```

---

## Office Panel API

**Route:** `/api/office`

Controls the visual Office Panel showing animated agent avatars.

### Notify Agent Spawned
```
POST /api/office/agents
```
**Body:**
```json
{
  "name": "Agent Alice",
  "spawnedBy": "Bob"
}
```
Triggers walk-in animation in the Office Panel.

### Notify Agent Departed
```
DELETE /api/office/agents/{name}
```
Triggers exit animation.

### List Active Office Agents
```
GET /api/office/agents
```

### Cleanup Stale Agents
```
DELETE /api/office/agents/cleanup?olderThanMinutes=30
```
Removes ghost agents older than the specified threshold.

### Clear All Agents
```
DELETE /api/office/agents/clear-all
```
Force-clear all office agents (nuclear option).

---

## Spawn API

**Route:** `/api/spawn`

### Spawn Headless Agent
```
POST /api/spawn/agent
```
**Body:**
```json
{
  "agentName": "Agent Alice",
  "workingDir": "H:\\Dev\\MyProject",
  "initialPrompt": "Review the codebase",
  "mcpConfigPath": "optional-config-path",
  "spawnerName": "Bob",
  "taskDescription": "Code review task",
  "subagentType": "general-purpose"
}
```
Spawns a headless AgentProcess with piped stdin/stdout. Conversation displayed in Agent Panel.

### Spawn Terminal
```
POST /api/spawn/terminal
```
**Body:**
```json
{
  "agentName": "Alice",
  "projectId": "optional-project-id",
  "workingDir": "H:\\Dev\\MyProject"
}
```
Spawns a new ConPTY terminal with Claude Code. If `projectId` is provided, resolves the project's source path as working directory. Used by ClaudeRemote.

---

## Team API

**Route:** `/api/team`

### Get All Profiles
```
GET /api/team/profiles
```
Returns all team member profiles with online status. Used by ClaudeRemote for agent picker.

### Get Team Roster
```
GET /api/team/roster?projectPath=H:\Dev\MyProject
```
Returns the team roster for a project merged with profile data (name, preferred model, agent instructions, role, skills).

---

## Debug API

**Route:** `/api/debug`

### Get Debug Logs
```
GET /api/debug/logs?count=50&offset=0&source=InboxMonitor&level=Info&search=nudge
```
All query params optional. Returns filtered, paginated log entries.

### Clear Debug Logs
```
DELETE /api/debug/logs
```

### Pause Logging
```
POST /api/debug/pause
```

### Resume Logging
```
POST /api/debug/resume
```

### Get Debug Status
```
GET /api/debug/status
```
Returns count, paused state, capacity, current log file path.

### List Log Files
```
GET /api/debug/files
```
Lists all available log files with size and current-file indicator.

### Read Log File
```
GET /api/debug/files/{fileName}?lines=200&search=AgentPanel
```
Read a previous log file by filename with optional line limit and search filter.

---

## Search API

**Route:** `/api/search`

Powered by bundled ripgrep (`tools/rg.exe`).

### Search Code Content
```
POST /api/search/content
```
**Body:**
```json
{
  "pattern": "class.*Service",
  "path": "H:\\Dev\\MyProject",
  "caseInsensitive": false,
  "glob": "*.cs",
  "maxCount": 10,
  "context": 2
}
```
Optional params: `multiline`, `fixedStrings`, `fileType`, `before`, `after`, `filesWithMatches`, `count`.

**Response:**
```json
{
  "success": true,
  "matchCount": 15,
  "matches": [
    {
      "filePath": "Services/RipgrepService.cs",
      "lineNumber": 10,
      "text": "    public class RipgrepService"
    }
  ],
  "stats": {
    "matchedLines": 15,
    "searchedFiles": 80,
    "elapsedMs": 12.3
  }
}
```

### Find Files
```
POST /api/search/files
```
**Body:**
```json
{
  "path": "H:\\Dev\\MyProject",
  "glob": "*.cs",
  "fileType": "cs"
}
```

### Check Search Status
```
GET /api/search/status
```
**Response:** `{ "available": true }`

---

## Notifications API

**Route:** `/api/notifications`

Receives Claude Code runtime notifications from hooks. Rate-limited to 100/minute.

### Post Notification
```
POST /api/notifications
```
**Body:**
```json
{
  "notification_type": "tool_error",
  "title": "Build Failed",
  "message": "Compilation error in MainForm.cs",
  "session_id": "session-abc",
  "agent_name": "Alice",
  "cwd": "H:\\Dev\\MyProject"
}
```
Stores in DB, fires broker event, and forwards to ClaudeRemote (port 5100) for phone push notifications.

### Get Notification History
```
GET /api/notifications?limit=50&unreadOnly=false
```

### Mark Notification as Read
```
POST /api/notifications/{id}/read
```

### Get Unread Count
```
GET /api/notifications/unread-count
```
**Response:** `{ "count": 3 }`

---

## Owner Profile API

**Route:** `/api/owner-profile`

### Get Profile
```
GET /api/owner-profile
```
Returns git identity and GitHub config.
**Response:**
```json
{
  "configured": true,
  "fullName": "John Doe",
  "email": "john@example.com",
  "gitHubUsername": "johndoe",
  "hasGitHubToken": true,
  "createdAt": "...",
  "updatedAt": "..."
}
```

### Get GitHub Token
```
GET /api/owner-profile/github-token
```
Returns the stored GitHub token for agent use during git operations. Returns 404 if not configured.

---

## Gateway API

**Route:** `/api/gateway`

Queries MCP Gateway server status and discovered tools from the gateway's SQLite database.

### List Gateway Servers
```
GET /api/gateway/servers
```
Returns all backend servers with connection status and tool counts.

### List Discovered Tools
```
GET /api/gateway/tools?server=sqlite&profile=default
```
Both params optional. Without `server`, returns all tools grouped by server.

### Get Tool Schema
```
GET /api/gateway/tools/{server__toolname}
```
Returns full schema for a specific tool by namespaced name (e.g., `sqlite__query`).

---

## Companions API

**Route:** `/api/companions`

### Get Companion Status
```
GET /api/companions/status
```
Returns the status of all configured companion processes.

---

## Terminal Stream API

WebSocket endpoint for real-time terminal I/O streaming.

### Stream Terminal I/O (WebSocket)
```
GET /api/terminal/{id}/stream
```
Upgrades to WebSocket. Protocol:
- **Binary frames:** Raw terminal I/O (VT/ANSI escape sequences)
- **Text frames:** JSON control messages (resize, disconnect, status)

### List Active Streams
```
GET /api/terminal/streams
```
Returns active terminal streams with subscriber counts.

---

## XAML Preview API

**Route:** `/api/xaml`

### Render XAML to Image
```
POST /api/xaml/render
```
**Body:**
```json
{
  "xaml": "<Border><TextBlock Text='Hello'/></Border>",
  "width": 520,
  "height": 400
}
```
Renders WPF XAML to a PNG image on an STA thread. Returns base64-encoded image.
**Response:** `{ "success": true, "imageBase64": "...", "width": 520, "height": 400 }`

---

## Agent Panels API

**Route:** `/api/agent-panels`

### Close Agent Panel
```
POST /api/agent-panels/close
```
**Body:**
```json
{
  "transcriptPath": "C:\\path\\to\\transcript.jsonl"
}
```
Called by the SubagentStop hook when a subagent finishes. Closes the corresponding Agent Panel.

---

## Example Usage from Claude Code

```bash
# List tasks
curl http://localhost:5050/api/tasks

# Create a task
curl -X POST http://localhost:5050/api/tasks \
  -H "Content-Type: application/json" \
  -d '{"title":"Test","description":"Testing API","createdBy":"Claude","status":"todo"}'

# Send a message with priority
curl -X POST http://localhost:5050/api/messaging/send \
  -H "Content-Type: application/json" \
  -d '{"fromTerminalId":"abc123","to":"Alice","message":"Hello!","priority":"high"}'

# Get inbox (unread only)
curl http://localhost:5050/api/tasks/inbox/john?unreadOnly=true

# Search code
curl -X POST http://localhost:5050/api/search/content \
  -H "Content-Type: application/json" \
  -d '{"pattern":"class.*Service","path":".","glob":"*.cs"}'
```

## Notes

- The REST API runs on the same port (5050) as the main application.
- MCP tools (`mcp__multiterminal__*`) wrap these REST endpoints and remain the preferred interface for Claude Code agents.
- All endpoints return JSON responses.
- Error responses follow the pattern: `{ "error": "description" }`.
