# Claude Code Hooks Integration with MultiTerminal

**Date:** 2026-02-06
**Status:** Analysis Complete
**Author:** Research by Claude (Bob)

---

## Executive Summary

MultiTerminal **already has hooks infrastructure** and is currently using `SubagentStart` and `SubagentStop` hooks for activity tracking. The new `TeammateIdle` and `TaskCompleted` hooks **were added in Claude Code v2.1.33** (released recently) but are not yet in the official documentation. This document provides research on both existing hooks AND integration strategies for the new hooks.

---

## Part 1: Available Claude Code Hooks (Current State)

### Hooks Currently Available

According to Claude Code documentation and release notes (as of v2.1.33):

| Hook Event | Triggers When | Available Data | Status |
|------------|---------------|----------------|--------|
| **SubagentStart** | A subagent is spawned | `agent_id`, `agent_type`, `description`, `prompt` | ✅ Documented |
| **SubagentStop** | A subagent finishes | `agent_id`, `agent_type`, `agent_transcript_path`, `success` | ✅ Documented |
| **Stop** | Main Claude Code agent finishes responding | Standard hook data | ✅ Documented |
| **Notification** | Various notifications fire (with matchers) | Depends on notification type | ✅ Documented |
| **TeammateIdle** | A teammate agent becomes idle | TBD (not yet documented) | ⚠️ New in v2.1.33 |
| **TaskCompleted** | A task is completed | TBD (not yet documented) | ⚠️ New in v2.1.33 |

**Source for new hooks:** https://github.com/anthropics/claude-code/releases/tag/v2.1.33

### NEW: TeammateIdle and TaskCompleted (v2.1.33)

**Status:** Added in Claude Code v2.1.33, but **not yet fully documented**

**What We Know:**
- **TeammateIdle** - Fires when a teammate agent becomes idle
- **TaskCompleted** - Fires when a task is completed
- Both are designed specifically for multi-agent workflows
- Part of the agent teammate functionality improvements

**What We DON'T Know Yet:**
- ❌ Exact data payload structure
- ❌ Configuration syntax
- ❌ Trigger conditions (is TaskCompleted tied to Task tool? or general completion?)
- ❌ How "teammate" is defined (terminals? subagents? both?)
- ❌ Whether these work with experimental Agent Teams feature or standalone

**Experimentation Needed:**
We need to test these hooks to understand their behavior. See Part 8 for experimental approach.

---

### Special: Notification Hook with `idle_prompt` Matcher

The `Notification` hook can be filtered using matchers:
```json
{
  "Notification": {
    "matcher": "idle_prompt",
    "command": "node",
    "args": ["path/to/idle-detector.js"]
  }
}
```

This effectively detects when Claude enters an idle state (closest to "TeammateIdle" concept).

### Experimental: Agent Teams Feature

Requires environment variable: `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=true`

**Capabilities:**
- Inter-teammate messaging (`message`, `broadcast`)
- Task list coordination
- Automatic message delivery between team members

**Note:** This is experimental and may change without notice.

---

## Part 2: MultiTerminal's Current Hook Implementation

### Already Implemented

MultiTerminal has a **production-ready hooks system** deployed via `Install-MultiTerminalIntegration.ps1`:

#### Current Hooks

**1. activity-hook.js**
- **Listens to:** `ToolUse`, `SubagentStart`, `SubagentStop`
- **Purpose:** Records activity to the Activity Feed
- **Integration:** Writes to `tasks.db` → Activity Panel displays events
- **Features:**
  - Build detection (dotnet, npm, cargo, go, etc.)
  - Test execution tracking
  - Subagent lifecycle monitoring
  - Skips noise (Read, Glob, Grep, ToolSearch)

**2. pool-context.js**
- **Listens to:** `Prompt`
- **Purpose:** Injects startup context about MultiTerminal environment
- **Integration:** Provides terminal name, identity, and team context
- **Features:**
  - Pre-registration with MCP server
  - Environment variable injection
  - Team awareness messaging

#### Hook Configuration

Located in: `~/.claude/hooks/hooks.json`

```json
{
  "ToolUse": {
    "command": "node",
    "args": ["C:\\...\\activity-hook.js"]
  },
  "SubagentStart": {
    "command": "node",
    "args": ["C:\\...\\activity-hook.js"]
  },
  "SubagentStop": {
    "command": "node",
    "args": ["C:\\...\\activity-hook.js"]
  },
  "Prompt": {
    "command": "node",
    "args": ["C:\\...\\pool-context.js"]
  }
}
```

### Current Activity Tracking Architecture

**Flow:**
1. Hook fires (e.g., `SubagentStop`)
2. `activity-hook.js` processes event
3. Writes to SQLite (`tasks.db` → `activity_feed` table)
4. `ActivityService.cs` reads from database
5. `MessageBroker.ActivityRecorded` event fires
6. UI (`ActivityPanel`) updates in real-time

**Activity Types Tracked:**
- `SUBAGENT_START` - When subagent spawns
- `SUBAGENT_COMPLETE` - When subagent finishes successfully
- `SUBAGENT_FAILED` - When subagent fails
- `BUILD_START` - Build command detected
- `BUILD_SUCCESS` - Build completes successfully
- `BUILD_FAILED` - Build fails
- `TEST_START` - Test command detected
- `TEST_SUCCESS` - Tests pass
- `TEST_FAILED` - Tests fail

---

## Part 3: Integration Opportunities

### 1. Enhance Idle Detection (Teammate Status)

**Current Gap:** No automatic detection of when a terminal becomes idle.

**What We Have:**
- `Notification` hook with `idle_prompt` matcher (detects idle state)
- `ActivityService` with staleness detection (5-minute threshold)

**Integration Design:**

```javascript
// idle-detection-hook.js
// Listens to: Notification (matcher: idle_prompt)
const Database = require('better-sqlite3');
const db = new Database(process.env.APPDATA + '/multiterminal/tasks.db');

function onIdleDetected() {
  const terminalName = process.env.MULTITERMINAL_NAME;
  const timestamp = new Date().toISOString();

  // Update activity status to idle
  db.prepare(`
    INSERT OR REPLACE INTO terminal_activities
    (terminal, status, activity, updated_at)
    VALUES (?, 'idle', 'Waiting for user input', ?)
  `).run(terminalName, timestamp);

  // Optional: Trigger task reassignment if terminal was busy
  // Optional: Notify team members via MessageBroker
}

module.exports = onIdleDetected;
```

**Configuration Addition:**
```json
{
  "Notification": {
    "matcher": "idle_prompt",
    "command": "node",
    "args": ["C:\\...\\idle-detection-hook.js"]
  }
}
```

**Benefits:**
- Real-time idle status updates in team activity view
- Enables automatic task reassignment
- Better load balancing across team members

---

### 2. Enhanced Task Completion Detection

**Current State:** Task completion is tracked manually via MCP tools.

**What We Have:**
- `SubagentStop` hook (detects when subagent finishes)
- Task status in database (`pending`, `in_progress`, `completed`)
- `MessageBroker.TasksUpdated` event

**Integration Design:**

**Option A: Subagent-Based Detection**
```javascript
// task-completion-hook.js
// Listens to: SubagentStop

function onSubagentStop(hookData) {
  const agentType = hookData.agent_type;
  const success = hookData.success !== false;
  const terminalName = process.env.MULTITERMINAL_NAME;

  // Check if this subagent was working on a task
  const db = new Database(process.env.APPDATA + '/multiterminal/tasks.db');
  const currentTask = db.prepare(`
    SELECT id FROM kanban_tasks
    WHERE assigned_to = ? AND status = 'in_progress'
    LIMIT 1
  `).get(terminalName);

  if (currentTask && success) {
    // Record task completion event
    recordActivity('TASK_COMPLETED', terminalName,
      `Completed task ${currentTask.id}`, 'success');

    // Optional: Auto-update task status
    // Optional: Notify blocked tasks
    // Optional: Broadcast to team
  }
}
```

**Option B: Stop Hook (Main Agent)**
```javascript
// stop-hook.js
// Listens to: Stop (main agent finishes)

function onStop() {
  const terminalName = process.env.MULTITERMINAL_NAME;

  // Check for tasks marked as completed by the agent
  // Trigger dependent tasks
  // Send notifications
  // Update team activity
}
```

**Configuration Addition:**
```json
{
  "Stop": {
    "command": "node",
    "args": ["C:\\...\\stop-hook.js"]
  }
}
```

**Benefits:**
- Automatic task state transitions
- Trigger dependent tasks when blockers complete
- Real-time team notifications
- Reduces manual status updates

---

### 3. Automatic Workflow Orchestration

**Vision:** Tasks flow automatically to idle agents when dependencies complete.

**Architecture:**

```
[SubagentStop Hook]
       ↓
[Task Completion Detected]
       ↓
[Check for Dependent Tasks] → tasks.blockedBy field
       ↓
[Find Idle Team Members] → terminal_activities.status = 'idle'
       ↓
[Auto-Assign Task] → Update kanban_tasks.assigned_to
       ↓
[Send Message to Idle Agent] → MCP multiterminal__send_message
```

**Implementation:**

```javascript
// workflow-orchestrator-hook.js
// Listens to: SubagentStop

async function orchestrateWorkflow(hookData) {
  const db = new Database(DB_PATH);
  const currentTerminal = process.env.MULTITERMINAL_NAME;

  // 1. Get completed task
  const completedTask = db.prepare(`
    SELECT id FROM kanban_tasks
    WHERE assigned_to = ? AND status = 'in_progress'
  `).get(currentTerminal);

  if (!completedTask) return;

  // 2. Find tasks blocked by this task
  const unblockedTasks = db.prepare(`
    SELECT id, title FROM kanban_tasks
    WHERE status = 'pending'
    AND blocked_by LIKE ?
  `).all(`%${completedTask.id}%`);

  if (unblockedTasks.length === 0) return;

  // 3. Find idle team members
  const idleMembers = db.prepare(`
    SELECT terminal FROM terminal_activities
    WHERE status = 'idle'
    AND terminal != ?
    ORDER BY updated_at ASC
  `).all(currentTerminal);

  if (idleMembers.length === 0) return;

  // 4. Auto-assign tasks to idle members
  for (let i = 0; i < Math.min(unblockedTasks.length, idleMembers.length); i++) {
    const task = unblockedTasks[i];
    const member = idleMembers[i];

    // Update task assignment
    db.prepare(`
      UPDATE kanban_tasks
      SET assigned_to = ?, status = 'ready'
      WHERE id = ?
    `).run(member.terminal, task.id);

    // Notify via MCP (if MCP server is available)
    // This would require calling the MCP server's send_message endpoint
    recordActivity('TASK_ASSIGNED', 'System',
      `Auto-assigned "${task.title}" to ${member.terminal}`, 'info');
  }

  db.close();
}
```

**Benefits:**
- Zero manual task assignment overhead
- Optimal team utilization
- Faster feature delivery
- Self-organizing team behavior

---

### 4. Integration with Existing MultiTerminal Features

#### A. Activity Feed Enhancement

**Current:** Manual activity recording via MCP tools
**Enhancement:** Automatic activity recording for all hook events

**New Activity Types:**
- `IDLE_DETECTED` - Terminal becomes idle
- `ACTIVE_DETECTED` - Terminal resumes work
- `TASK_AUTO_ASSIGNED` - Workflow orchestrator assigns task
- `DEPENDENCY_RESOLVED` - Blocked task becomes unblocked

#### B. Real-Time Status Updates

**Current:** Periodic polling (if implemented) or manual updates
**Enhancement:** Push-based updates via hooks

**Flow:**
```
[Hook Fires] → [Update Database] → [MessageBroker Event] → [SignalR Push] → [UI Update]
```

#### C. Helper System Integration

**Current:** Manual helper requests via MCP tools
**Enhancement:** Automatic helper suggestions based on idle detection

**Logic:**
```javascript
if (terminal is idle && task queue has pending tasks) {
  suggestHelperOpportunity(terminal, task);
}
```

#### D. Stale Task Detection

**Current:** Periodic timer checks for stale tasks
**Enhancement:** Integrate with `SubagentStop` to reset staleness timers

**Logic:**
```javascript
onSubagentStop() {
  if (task still in_progress) {
    resetStalenessTimer(task);
  } else {
    startStalenessTimer(nextTask);
  }
}
```

---

## Part 4: Implementation Roadmap

### Phase 1: Idle Detection (Quick Win)
- **Effort:** 2-3 hours
- **Files:** New `idle-detection-hook.js`, update `hooks.json`
- **Benefit:** Real-time idle status in team activity

### Phase 2: Enhanced Subagent Tracking
- **Effort:** 4-6 hours
- **Files:** Enhance `activity-hook.js`, add `SubagentStop` logic
- **Benefit:** Better visibility into agent work completion

### Phase 3: Task Completion Events
- **Effort:** 6-8 hours
- **Files:** New `task-completion-hook.js`, update `MessageBroker.cs`
- **Benefit:** Automatic task state transitions

### Phase 4: Workflow Orchestration
- **Effort:** 8-12 hours
- **Files:** New `workflow-orchestrator-hook.js`, integrate with MCP
- **Benefit:** Self-organizing task assignment

### Phase 5: UI Integration
- **Effort:** 4-6 hours
- **Files:** Update `ActivityPanel`, `TasksPanel` UI
- **Benefit:** Real-time push updates, no polling needed

---

## Part 5: Technical Considerations

### Hook Execution Environment

**Context:**
- Hooks run in separate Node.js processes
- No direct access to MultiTerminal C# code
- Communication via SQLite database or HTTP (MCP)

**Constraints:**
- Must be fast (hooks block Claude briefly)
- Must be resilient (errors should not break Claude)
- Must handle missing dependencies gracefully

### Database Concurrency

**Challenge:** Multiple hooks writing to same SQLite database

**Solution:**
- Use WAL mode (Write-Ahead Logging)
- Keep transactions short
- Use prepared statements
- Handle SQLITE_BUSY errors with retry

### Error Handling

**Best Practices:**
```javascript
try {
  // Hook logic
} catch (err) {
  // Log error but don't throw
  // Hooks must never break Claude
  console.error('[Hook Error]', err);
  return; // Silent failure
}
```

### Testing Strategy

**1. Unit Tests**
- Test hook logic in isolation
- Mock SQLite database
- Verify correct SQL queries

**2. Integration Tests**
- Test hook → database → MessageBroker flow
- Verify UI updates
- Test concurrent hook execution

**3. Manual Testing**
- Trigger hooks in live Claude Code session
- Verify Activity Feed updates
- Check team activity status

---

## Part 6: Migration Path

### Current State → Enhanced State

**Step 1:** Add idle detection hook
- No changes to existing hooks
- Pure additive enhancement

**Step 2:** Enhance activity-hook.js
- Backward compatible
- Adds task completion detection

**Step 3:** Add workflow orchestrator
- Optional feature (can be disabled)
- Requires MCP integration

**Step 4:** Update UI for push updates
- Progressive enhancement
- Falls back to existing polling

**Rollout Strategy:**
- Deploy hooks via `Install-MultiTerminalIntegration.ps1`
- Feature flags for optional enhancements
- Gradual rollout per team member

---

## Part 7: Alternative: Experiment with Agent Teams

### If CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS is Available

**Direct Inter-Agent Communication:**
```javascript
// Instead of SQLite + MCP server
// Use built-in Claude Code agent messaging

claudeCode.message(teammateId, {
  type: 'task_completed',
  taskId: 'abc123',
  completedBy: 'Alice'
});

claudeCode.broadcast({
  type: 'idle_detected',
  terminal: 'Diana'
});
```

**Benefits:**
- Lower latency (no database round-trip)
- Built-in message delivery guarantees
- Simpler architecture

**Risks:**
- Experimental feature may change
- Less persistence than SQLite approach
- Requires all team members on compatible Claude version

**Recommendation:** Stick with SQLite + MCP approach for production reliability.

---

## Part 8: Experimenting with New Hooks (v2.1.33)

### Discovery Process for TeammateIdle and TaskCompleted

Since these hooks are undocumented, we need to discover their behavior experimentally.

#### Step 1: Create Test Hook Scripts

**test-teammate-idle.js**
```javascript
#!/usr/bin/env node
/**
 * Test hook for TeammateIdle event (v2.1.33+)
 */

const fs = require('fs');
const path = require('path');

// Log all data to discover payload structure
const logFile = path.join(process.env.APPDATA, 'multiterminal', 'hook-test-teammate-idle.log');

try {
  const timestamp = new Date().toISOString();
  const hookData = JSON.parse(process.argv[2] || '{}');
  const allEnvVars = Object.keys(process.env)
    .filter(k => k.startsWith('CLAUDE_') || k.startsWith('MULTITERMINAL_'))
    .reduce((acc, k) => ({ ...acc, [k]: process.env[k] }), {});

  const logEntry = {
    timestamp,
    event: 'TeammateIdle',
    hookData,
    envVars: allEnvVars,
    args: process.argv
  };

  fs.appendFileSync(logFile, JSON.stringify(logEntry, null, 2) + '\n---\n');
} catch (err) {
  fs.appendFileSync(logFile, `ERROR: ${err.message}\n---\n`);
}
```

**test-task-completed.js**
```javascript
#!/usr/bin/env node
/**
 * Test hook for TaskCompleted event (v2.1.33+)
 */

const fs = require('fs');
const path = require('path');

const logFile = path.join(process.env.APPDATA, 'multiterminal', 'hook-test-task-completed.log');

try {
  const timestamp = new Date().toISOString();
  const hookData = JSON.parse(process.argv[2] || '{}');
  const allEnvVars = Object.keys(process.env)
    .filter(k => k.startsWith('CLAUDE_') || k.startsWith('MULTITERMINAL_'))
    .reduce((acc, k) => ({ ...acc, [k]: process.env[k] }), {});

  const logEntry = {
    timestamp,
    event: 'TaskCompleted',
    hookData,
    envVars: allEnvVars,
    args: process.argv
  };

  fs.appendFileSync(logFile, JSON.stringify(logEntry, null, 2) + '\n---\n');
} catch (err) {
  fs.appendFileSync(logFile, `ERROR: ${err.message}\n---\n`);
}
```

#### Step 2: Add to hooks.json

**~/.claude/hooks/hooks.json**
```json
{
  "TeammateIdle": {
    "command": "node",
    "args": ["C:\\path\\to\\test-teammate-idle.js"]
  },
  "TaskCompleted": {
    "command": "node",
    "args": ["C:\\path\\to\\test-task-completed.js"]
  }
}
```

#### Step 3: Trigger Events and Observe

**Scenarios to Test:**

**For TeammateIdle:**
1. Start Claude Code session
2. Complete a task (using Task tool)
3. Let Claude go idle (wait for "idle_prompt")
4. Check log file for data

**For TaskCompleted:**
1. Create a task with `TaskCreate` tool
2. Update task status to `in_progress`
3. Update task status to `completed`
4. Check log file for data

**Alternative triggers to test:**
- Subagent completion
- Main agent Stop event
- Manual task updates via MCP

#### Step 4: Analyze Log Files

**Questions to Answer:**
1. What's in `hookData`? (task ID, agent ID, terminal ID, status, etc.)
2. Are environment variables set?
3. How often does it fire? (once per event? multiple times?)
4. Does it work with MultiTerminal's task system? Or only Claude's internal tasks?
5. Can we distinguish between different teammates?

#### Step 5: Document Findings

Once we know the payload structure, we can:
1. Update this document with official schemas
2. Build production hooks that use the data
3. Integrate with MultiTerminal's existing infrastructure

### Integration Once Behavior is Known

**If TeammateIdle provides terminal/agent identification:**
```javascript
// idle-handler-v2.js (using new hook)
function handleTeammateIdle(hookData) {
  const teammate = hookData.teammate_id || hookData.agent_id;
  const db = new Database(DB_PATH);

  // Update status
  db.prepare(`
    INSERT OR REPLACE INTO terminal_activities
    (terminal, status, activity, updated_at)
    VALUES (?, 'idle', 'Idle (detected by hook)', ?)
  `).run(teammate, new Date().toISOString());

  // Trigger workflow orchestration
  assignTasksToIdleTeammate(teammate);
}
```

**If TaskCompleted provides task details:**
```javascript
// task-completed-handler-v2.js (using new hook)
function handleTaskCompleted(hookData) {
  const taskId = hookData.task_id;
  const completedBy = hookData.completed_by;
  const db = new Database(DB_PATH);

  // Record activity
  recordActivity('TASK_COMPLETED', completedBy,
    `Completed task ${taskId}`, 'success');

  // Unblock dependent tasks
  const unblockedTasks = db.prepare(`
    SELECT id FROM kanban_tasks
    WHERE blocked_by LIKE ?
  `).all(`%${taskId}%`);

  // Notify team
  for (const task of unblockedTasks) {
    recordActivity('TASK_UNBLOCKED', 'System',
      `Task ${task.id} is now unblocked`, 'info');
  }
}
```

### Compatibility Check

**Minimum Version Required:** Claude Code v2.1.33+

**Check User's Version:**
```bash
claude --version
```

**If version < 2.1.33:**
- Fall back to `Notification` hook with `idle_prompt` matcher (for idle detection)
- Fall back to `SubagentStop` hook + task status inference (for completion detection)

---

## Conclusion

### What We Learned

1. **TeammateIdle and TaskCompleted hooks EXIST** (added in Claude Code v2.1.33) but are **not yet documented**
2. **MultiTerminal already has robust hooks infrastructure** using `SubagentStart`, `SubagentStop`, and `ToolUse`
3. **We can experiment with the new hooks** using test scripts to discover their behavior
4. **Integration opportunities are rich** and the new hooks may provide exactly what we need
5. **We have fallback options** using existing documented hooks if the new ones don't meet our needs

### What We Can Build

| Feature | Feasibility | Effort | Impact |
|---------|-------------|--------|--------|
| Idle Detection | ✅ High | Low | Medium |
| Task Completion Events | ✅ High | Medium | High |
| Workflow Orchestration | ⚠️ Medium | High | Very High |
| Real-Time Push Updates | ✅ High | Medium | High |

### Recommended Next Steps

**UPDATED based on v2.1.33 release:**

1. **Immediate:** Check Claude Code version (`claude --version`)
   - If v2.1.33+: Proceed with experimentation
   - If older: Upgrade or use fallback approach

2. **Experimentation Phase (if v2.1.33+):**
   - Create test hook scripts (Part 8)
   - Add to `~/.claude/hooks/hooks.json`
   - Run test scenarios to discover behavior
   - Document payload structures

3. **Integration Phase (once behavior is known):**
   - Build production hooks using new events
   - Integrate with MultiTerminal Activity Feed
   - Wire up workflow orchestration
   - Update UI for real-time updates

4. **Fallback Phase (if v2.1.33 unavailable or hooks insufficient):**
   - Implement idle detection using `Notification` hook (Phase 1)
   - Enhance subagent tracking with `SubagentStop` (Phase 2)
   - Build task completion inference logic (Phase 3)

### Questions for User

1. Should we prioritize idle detection or task completion events first?
2. Do you want automatic workflow orchestration, or prefer manual task assignment?
3. Should we experiment with Agent Teams feature despite it being experimental?
4. What's the team size? (Affects orchestration complexity)

---

**Next Actions:**
- [ ] User reviews this analysis
- [ ] User decides on priorities
- [ ] Implement Phase 1 (if approved)
- [ ] Test in live environment
- [ ] Iterate based on feedback

---

**End of Analysis**
