# Build Logging to Activity Feed - Agent Guide

## Overview

MultiTerminal has **built-in build logging** that automatically records build events to the Activity Feed. This guide shows you when and how to use these tools.

## Why Log Builds?

- **Team Visibility** - Everyone sees what's being built and results
- **Activity Timeline** - Builds appear in the Mission Control Activity Feed
- **Failure Tracking** - Failed builds are highlighted for quick attention
- **Session History** - Build results persist across sessions

## Quick Start

### 1. Before Running a Build

Call `record_build_start` **BEFORE** executing your build command:

```json
{
  "terminal_name": "Charlie",
  "project_name": "MultiTerminal.sln"
}
```

### 2. After Build Completes

Call `record_build` **AFTER** the build finishes:

```json
{
  "terminal_name": "Charlie",
  "success": true,
  "project_name": "MultiTerminal.sln",
  "details": "Build succeeded with 0 warnings"
}
```

Or if it failed:

```json
{
  "terminal_name": "Charlie",
  "success": false,
  "project_name": "MultiTerminal.sln",
  "details": "Error CS1002: ; expected"
}
```

## Complete Example Workflow

```bash
# 1. Load the MCP tool
ToolSearch(query: "select:mcp__multiterminal__record_build_start")

# 2. Record build start
mcp__multiterminal__record_build_start(
  terminal_name: "Charlie",
  project_name: "MultiTerminal.sln"
)

# 3. Run the build
Bash(command: "dotnet build MultiTerminal.sln")

# 4. Record the result
mcp__multiterminal__record_build(
  terminal_name: "Charlie",
  success: true,
  project_name: "MultiTerminal.sln",
  details: "Build succeeded in 15 seconds"
)
```

## When to Use

✅ **DO use for:**
- Solution builds (`dotnet build`, `msbuild`)
- Project builds (building a specific .csproj)
- Release builds
- Any build that might fail and need team attention

❌ **DON'T use for:**
- Simple file operations (copy, move)
- Running tests (use activity logging instead)
- Non-build commands (git, npm install, etc.)

## What Gets Logged

### Database (activity_feed table)
- Timestamp
- Activity type: `BUILD_STARTED`, `BUILD_SUCCEEDED`, `BUILD_FAILED`
- Actor (your terminal name)
- Summary text
- Optional details (error messages, warnings)

### Activity Feed UI
- Appears in Mission Control Activity Panel
- Blue "Build" type indicator
- Filterable by "Build" chip
- Real-time updates as builds complete

### PoolCoordinator (state.jsonl)
- Cross-session persistence
- Team activity tracking
- Resumable session context

## Error Handling

If the build command fails, always log the failure:

```bash
# Run build and capture exit code
dotnet build MyProject.sln
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0) {
    # Success
    record_build(terminal_name: "Charlie", success: true, ...)
} else {
    # Failure - include error details
    record_build(terminal_name: "Charlie", success: false,
                 details: "Build failed with exit code $exitCode")
}
```

## Tips & Best Practices

1. **Always log both start and end** - Helps track build duration
2. **Include meaningful details** - Error messages, warning counts, build time
3. **Use consistent project names** - Makes filtering easier
4. **Log failures immediately** - Don't skip logging when builds fail
5. **Keep details concise** - First error message is usually enough

## Related Tools

- `update_activity` - General activity tracking (not build-specific)
- `record_activity` - Custom activity events
- `record_feed_activity` - High-level plan/phase events

## Technical Details

**Backend:**
- Service: `ActivityFeedService.cs`
- Integration: `MessageBroker.cs`
- Tools: `ActivityTools.cs` (lines 128-210)

**Frontend:**
- UI: `ActivityPanel/panel.html`
- Renderer: `ActivityPanelDocument.cs`
- Event flow: MCP → MessageBroker → ActivityRecorded event → UI

**Database Schema:**
```sql
CREATE TABLE activity_feed (
    id INTEGER PRIMARY KEY,
    timestamp TEXT,
    activity_type TEXT,  -- BUILD_STARTED, BUILD_SUCCEEDED, BUILD_FAILED
    actor TEXT,
    summary TEXT,
    severity TEXT,       -- info, warning, error
    details_json TEXT
);
```

## Verification

To verify build logging is working:

1. Run a build with logging
2. Open Mission Control (Activity Panel)
3. Click the "Build" filter chip
4. See your build events with blue type indicator

---

**Created:** 2026-02-06
**Status:** Production-ready (feature fully implemented)
**Maintainer:** MultiTerminal Team
