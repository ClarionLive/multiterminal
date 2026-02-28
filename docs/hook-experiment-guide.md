# Hook Experiment Guide

**Date:** 2026-02-06
**Hooks:** TeammateIdle, TaskCompleted (Claude Code v2.1.33+)
**Status:** Experimenting
**Team:** Bob (TaskCompleted), Charlie (TeammateIdle)

---

## Setup Complete ✓

- [x] Test hooks created (`test-teammate-idle.js`, `test-task-completed.js`)
- [x] Hooks installed to `~/.claude/hooks/hooks.json`
- [x] Log files will be created in `%APPDATA%\multiterminal\`
  - `hook-test-teammate-idle.log`
  - `hook-test-task-completed.log`

---

## Experiment Plan

### Bob's Tasks: TaskCompleted Hook

**Goal:** Discover what triggers TaskCompleted and what data it provides

**Test Scenarios:**

1. **Task Tool Completion**
   - Create task: `TaskCreate(subject="Test task", description="Test", activeForm="Testing")`
   - Update to in_progress: `TaskUpdate(taskId="X", status="in_progress")`
   - Update to completed: `TaskUpdate(taskId="X", status="completed")`
   - **Expected:** Hook fires when status changes to completed
   - **Check log:** Look for taskId, subject, owner, etc.

2. **Subagent Completion**
   - Spawn subagent with Task tool: `Task(subagent_type="general-purpose", ...)`
   - Let subagent complete
   - **Expected:** Hook might fire when subagent finishes
   - **Check log:** Look for agent_id, agent_type, transcript_path

3. **Stop Event Completion**
   - Complete a response (let main agent finish)
   - **Expected:** Hook might fire when main agent stops
   - **Check log:** Look for any stop-related data

4. **Manual Task Completion via MCP**
   - Use MultiTerminal MCP task tools
   - Create and complete a kanban task
   - **Expected:** Hook might fire if integrated with MCP
   - **Check log:** Look for task data from MultiTerminal

### Charlie's Tasks: TeammateIdle Hook

**Goal:** Discover what triggers TeammateIdle and what data it provides

**Test Scenarios:**

1. **Natural Idle State**
   - Finish a response
   - Wait for idle_prompt (user sees prompt, no input)
   - **Expected:** Hook fires when Claude enters idle state
   - **Check log:** Look for terminal/agent identification

2. **After Subagent Completion**
   - Spawn subagent
   - Wait for subagent to finish
   - Go idle after subagent completes
   - **Expected:** Hook might fire for subagent or main agent going idle
   - **Check log:** Look for agent_type, who went idle

3. **Multi-Terminal Scenario**
   - Have multiple terminals (Bob and Charlie both active)
   - One goes idle, other stays active
   - **Expected:** Hook might fire with teammate identification
   - **Check log:** Look for which teammate went idle

4. **Task Completion → Idle Transition**
   - Complete a task using TaskUpdate
   - Immediately go idle after completion
   - **Expected:** Both hooks might fire (TaskCompleted + TeammateIdle)
   - **Check logs:** Compare timestamps, see correlation

---

## Data to Capture

### For TaskCompleted

**Look for these fields in hookData:**
- `task_id` - Task identifier
- `status` - Completion status
- `completed_by` - Agent/terminal who completed it
- `result` - Output or result of task
- `duration` - How long task took
- `success` - Boolean or status indicator
- `agent_id` - If completed by subagent
- `transcript_path` - If completed by subagent

**Environment variables to check:**
- `CLAUDE_*` - Any Claude-specific vars
- `MULTITERMINAL_*` - Our custom vars
- `TASK_*` - Any task-related vars

### For TeammateIdle

**Look for these fields in hookData:**
- `teammate_id` - Who went idle
- `agent_id` - Agent identifier
- `agent_name` - Friendly name
- `terminal_id` - Terminal identifier
- `last_activity` - Timestamp of last activity
- `idle_duration` - How long idle
- `previous_state` - What they were doing before idle

**Environment variables to check:**
- `CLAUDE_*` - Any Claude-specific vars
- `MULTITERMINAL_*` - Our custom vars
- `TEAMMATE_*` - Any teammate-related vars
- `AGENT_*` - Any agent identification vars

---

## Log Analysis Process

### Step 1: Trigger Events
Each person runs their test scenarios

### Step 2: Check Logs
```powershell
# Bob checks TaskCompleted log
Get-Content "$env:APPDATA\multiterminal\hook-test-task-completed.log"

# Charlie checks TeammateIdle log
Get-Content "$env:APPDATA\multiterminal\hook-test-teammate-idle.log"
```

### Step 3: Extract Payloads
Look for JSON structures in logs, identify:
- What triggered the hook?
- What data was provided?
- What environment variables were set?

### Step 4: Share Findings
Message each other with discoveries:
- "TaskCompleted fires when TaskUpdate status=completed"
- "Payload includes: { task_id, completed_by, result }"
- etc.

### Step 5: Document Schema
Once we know the structure, document it in:
- `docs/claude-hooks-integration.md`
- Create TypeScript/JSON schema definitions
- Update integration plans with actual data

---

## Expected Timeline

- **Setup:** 5-10 min (DONE)
- **Scenario Testing:** 20-30 min
- **Log Analysis:** 10-15 min
- **Documentation:** 10-15 min
- **Total:** 45-70 min

---

## Success Criteria

We'll know we're done when we can answer:

1. ✓ What triggers TaskCompleted?
2. ✓ What data does TaskCompleted provide?
3. ✓ What triggers TeammateIdle?
4. ✓ What data does TeammateIdle provide?
5. ✓ How do these integrate with MultiTerminal's task system?
6. ✓ Can we use them for workflow orchestration?

---

## Next Steps After Discovery

Once we understand the hooks:

1. **Update Integration Doc** - Add schemas and trigger conditions
2. **Build Production Hooks** - Replace test hooks with real implementations
3. **Wire to MessageBroker** - Integrate with Activity Feed
4. **Update UI** - Show real-time status from hooks
5. **Build Orchestration** - Auto-assign tasks based on hooks

---

## Notes

- Hooks must exit with code 0 (never break Claude)
- Logs append (use timestamps to track different test runs)
- Restart Claude sessions if hooks don't fire (need fresh load)
- If hooks never fire, they might be tied to Agent Teams feature (experimental)

---

**Status Updates:**

- **2026-02-06 [Time]** - Hooks installed, waiting for Charlie
- **2026-02-06 [Time]** - Starting TaskCompleted tests (Bob)
- **2026-02-06 [Time]** - Starting TeammateIdle tests (Charlie)
- **2026-02-06 [Time]** - Analysis complete, findings documented

---

**End of Guide**
