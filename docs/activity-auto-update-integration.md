# Auto-Update Integration Spec for Live Activity Tracking

This document specifies where activity tracking hooks should be integrated into existing MCP tools to automatically keep terminal activity status current.

## Overview

Activity updates should happen automatically when terminals perform actions, reducing manual `update_activity` calls. This is achieved by hooking into existing tool methods.

## Integration Points

### High Priority Hooks

#### 1. `register_terminal` (MessagingTools.cs:31-78)
**Trigger:** Terminal registration
**Activity Update:**
```
terminal: <name>
status: "idle"
activity: "Just registered"
task_id: null
plan_id: null
```
**Location:** After successful registration in `RegisterTerminal()` method

---

#### 2. `claim_task` (MessageBroker.cs:444-466)
**Trigger:** Terminal claims a Kanban task
**Activity Update:**
```
terminal: <assignee>
status: "working"
activity: "Working on: <task_title>"
task_id: <task_id>
plan_id: null (or current plan if in a plan)
```
**Location:** After `task.Assignee = assignee;` in `ClaimTask()` method

---

#### 3. `update_assignment_status` (PlanTools.cs:475-525)
**Trigger:** Assignment status changes
**Activity Update:**
| Assignment Status | Activity Status | Activity Text |
|-------------------|-----------------|---------------|
| `assigned` | `idle` | "Assigned to: <task_summary>" |
| `in_progress` | `working` | "Working on: <task_summary>" |
| `blocked` | `blocked` | "Blocked: <blocked_by>" |
| `done` | `idle` | "Completed: <task_summary>" |

**Location:** After `_db.SaveAssignment(assignment);` in `UpdateAssignmentStatus()` method

---

#### 4. `update_task_status` (MessageBroker.cs:471-498)
**Trigger:** Task status changes to "done"
**Activity Update (when status = "done"):**
```
terminal: <task.Assignee>
status: "idle"
activity: "Completed task: <task_title>"
task_id: null
```
**Location:** After `task.Status = status;` when status is "done"

---

### Medium Priority Hooks

#### 5. `assign_terminal` (PlanTools.cs:362-426)
**Trigger:** Terminal receives a new assignment
**Activity Update:**
```
terminal: <terminal_name>
status: "idle"
activity: "Assigned to plan: <task_summary>"
plan_id: <plan_id>
```
**Location:** After `_db.SaveAssignment(assignment);` in `AssignTerminal()` method

---

#### 6. `update_phase` (PlanTools.cs:169-273)
**Trigger:** Plan phase changes
**Activity Update (for leader only):**
```
terminal: <plan.LeaderId>
status: "working"
activity: "Leading phase: <new_phase>"
plan_id: <plan_id>
```
**Location:** After `_db.SavePlan(plan);` in `UpdatePhase()` method

---

### Low Priority Hooks (Nice-to-Have)

#### 7. Any tool call - Last Seen Update
**Trigger:** Any MCP tool invocation
**Update:** Only `updated_at` timestamp
**Implementation:** Could be done at the MCP server middleware level rather than individual tools

---

## Implementation Approach

### Option A: Direct Integration (Recommended for MVP)
Add activity update calls directly in each tool method after the primary action succeeds.

```csharp
// Example in ClaimTask
public ClaimTaskResult ClaimTask(string taskId, string assignee)
{
    // ... existing code ...
    task.Assignee = assignee;
    task.Status = "in_progress";

    // NEW: Auto-update activity
    _activityService.UpdateActivity(assignee, "working",
        $"Working on: {task.Title}", taskId, null);

    // ... rest of method ...
}
```

### Option B: Event-Based Integration (Cleaner, More Complex)
Use existing events in MessageBroker to trigger activity updates.

```csharp
// In MultiTerminalMcpServer.cs startup
broker.TerminalRegistered += (s, terminal) =>
    activityService.UpdateActivity(terminal.Name, "idle", "Just registered");

broker.TasksUpdated += (s, tasks) =>
    // Analyze which tasks changed and update relevant activities
```

**Recommendation:** Start with Option A for simplicity, refactor to Option B later if needed.

---

## New Service Required

### ActivityService

```csharp
public class ActivityService
{
    private readonly ActivityDatabase _db;

    public void UpdateActivity(
        string terminalName,
        string status,
        string activity,
        string taskId = null,
        string planId = null)
    {
        var record = new TerminalActivity
        {
            Terminal = terminalName,
            Status = status,
            Activity = activity,
            TaskId = taskId,
            PlanId = planId,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Save(record);
    }

    public List<TerminalActivity> GetTeamActivity()
    {
        return _db.GetAll();
    }
}
```

---

## Dependency Injection Setup

In `MultiTerminalMcpServer.cs`, add:

```csharp
builder.Services.AddSingleton<ActivityDatabase>();
builder.Services.AddSingleton<ActivityService>();
```

Tools that need activity updates will receive `ActivityService` via constructor injection:
- `MessagingTools` - for register_terminal
- `TaskTools` - for claim_task (via MessageBroker)
- `PlanTools` - for assignment/phase updates

---

## Staleness Handling

Activity records should be marked stale if not updated within 5 minutes:

```csharp
public List<TerminalActivity> GetTeamActivity()
{
    var activities = _db.GetAll();
    var staleThreshold = DateTime.UtcNow.AddMinutes(-5);

    foreach (var a in activities)
    {
        if (a.UpdatedAt < staleThreshold)
            a.Status = "unknown";
    }

    return activities;
}
```

---

## Summary: Files to Modify

| File | Changes |
|------|---------|
| `MCPServer/MultiTerminalMcpServer.cs` | Add DI for ActivityService |
| `MCPServer/Services/ActivityService.cs` | **NEW** - Activity tracking logic |
| `MCPServer/Services/ActivityDatabase.cs` | **NEW** - SQLite persistence |
| `MCPServer/Tools/MessagingTools.cs` | Hook register_terminal |
| `MCPServer/Tools/PlanTools.cs` | Hook assignment/phase updates |
| `MCPServer/Services/MessageBroker.cs` | Hook task claim/status changes |

---

## Design Checklist Item

This document covers: **"Identify auto-update integration points"**

Ready for team review and alignment.
