# Startup Context Injection

This document describes how the Task-Centric Memory System injects plan context into terminal sessions at startup.

## Overview

When a terminal starts, it receives contextual information about the active plan, including:
- Plan title and current phase
- Terminal's role and assignment
- Phase checklist items
- Any blocking constraints

This allows Claude Code sessions to immediately understand the team's current work without manual briefing.

## Architecture

### Context Generation

The `GenerateStartupContext(string terminalName)` method in `PlanDatabase.cs` generates formatted context:

```csharp
public string GenerateStartupContext(string terminalName)
{
    var plan = GetActivePlan();
    if (plan == null)
    {
        return "## No Active Plan\nNo plan is currently active. Create a new plan or pick up a KanbanTask.";
    }

    // Build context with plan, phases, and assignment info
    // ...
}
```

### Output Format

The generated context follows this structure:

```
## Active Plan: [Plan Title]
Phase: [phase] ([n]/[total]) | Leader: [leader name]
Your Role: [role]
Your Task: [task description]
Status: [assigned|in_progress|blocked]
Blocked By: [reason] (if applicable)

Checklist:
  [x] Completed item
  [ ] Pending item
```

### No Active Plan Scenario

When no plan is active, the context provides guidance:

```
## No Active Plan
No plan is currently active. Create a new plan or pick up a KanbanTask.
```

## Terminal Integration

### Environment Variables

Terminals receive identity via environment variables set in `ConPtyTerminal.cs`:

- `MULTITERMINAL_DOC_ID` - Unique document ID for the terminal window
- `MULTITERMINAL_NAME` - Friendly name (e.g., "Alice", "Bob")

### Startup Hook

The startup hook queries the active plan via MCP tools and injects the context as a system reminder at session start. This appears in the conversation context before the first user message.

## Key Files

| File | Purpose |
|------|---------|
| `Services/PlanDatabase.cs` | Context generation and plan queries |
| `Terminal/ConPtyTerminal.cs` | Environment variable injection |
| `MCPServer/Tools/PlanTools.cs` | MCP tools for plan management |

## Related Documentation

- [Test Isolation](test-isolation.md) - Testing considerations for database isolation
