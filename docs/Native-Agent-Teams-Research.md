# Native Agent Teams Research
**Date:** 2026-02-08
**Research by:** Bob

## Executive Summary

Anthropic launched native **agent teams** as a research preview on February 5, 2026 with Claude Opus 4.6. This is a system-level multi-agent coordination framework built directly into Claude Code. The key innovation is **tmux/iTerm2 integration** that spawns separate terminals for visual monitoring - but this doesn't work on Windows. MultiTerminal can become the **native Windows solution** for this capability.

## The Problem

### tmux/iTerm2 Display Modes

Claude Code supports two modes for agent teams:

1. **In-process mode** (default):
   - All teammates run inside one terminal
   - Use Shift+Up/Down to switch between them
   - Works on any platform
   - No visual separation

2. **Split-pane mode** (requires tmux/iTerm2):
   - Each teammate gets its own pane/window
   - See all agents working simultaneously
   - Click into any pane to interact
   - **Does NOT work on Windows**

### Windows Limitations

From official documentation:
> "Split-pane mode doesn't work with VS Code's integrated terminal, Windows Terminal, or Ghostty - you need a standalone terminal with tmux or iTerm2."

**Windows users are stuck with in-process mode only.**

## Native Agent Teams Architecture

### Core Components

**TeammateTool** - 13 operations organized into four categories:

**Team Lifecycle:**
- `spawnTeam` - Create new team with separate Claude instances
- `discoverTeams` - Find existing teams
- `cleanup` - Teardown team infrastructure

**Direct Coordination:**
- `requestJoin` / `approveJoin` / `rejectJoin` - Agent membership
- `write` - Peer-to-peer messaging
- `broadcast` - Message all members

**Task Approval:**
- `approvePlan` / `rejectPlan` - Hierarchical validation

**Graceful Shutdown:**
- `requestShutdown` / `approveShutdown` / `rejectShutdown` - Coordinated dissolution

### Infrastructure

```
~/.claude/teams/{team-name}/
├── config.json           # Team configuration with members array
└── messages/
    └── {session-id}/     # Inter-agent messages

~/.claude/tasks/          # Shared task list

Environment variables:
- CLAUDE_CODE_TEAM_NAME
- CLAUDE_CODE_AGENT_ID
- CLAUDE_CODE_AGENT_TYPE
- CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1 (required to enable)
```

### Context Injection

**The killer feature:** When agents message each other, context is automatically injected.
- Agent A sends message to Agent B
- Agent B receives message WITH relevant context from Agent A's work
- No manual context summarization needed

**Skills Injection:**
```yaml
skills:
  - api-conventions
  - error-handling-patterns
```
Full skill content is injected at spawn time.

### Five Coordination Patterns

1. **Leader** - Hierarchical task direction (like Alice + Diana)
2. **Swarm** - Parallel processing across similar work units
3. **Pipeline** - Sequential multi-stage workflows
4. **Council** - Multi-perspective decision-making
5. **Watchdog** - Quality monitoring and oversight

## The Opportunity: MultiTerminal as Windows Solution

### What We Already Have (80% Complete)

| Feature | Native (tmux/iTerm2) | MultiTerminal | Status |
|---------|---------------------|---------------|--------|
| Separate terminals | ✅ tmux panes | ✅ PowerShell windows | ✅ Have it |
| Visual monitoring | ✅ See all panes | ✅ Activity Panel UI | ✅ Have it |
| Inter-agent messaging | ✅ TeammateTool | ✅ MessageBroker | ✅ Have it |
| Shared task list | ✅ Task system | ✅ TaskDatabase | ✅ Have it |
| Direct interaction | ✅ Click pane | ⚠️ Send message UI | 🔨 Need enhancement |
| **Programmatic spawn** | ✅ spawnTeam | ❌ Not implemented | 🔨 **Need to build** |
| **Auto-registration** | ✅ Automatic | ⚠️ Manual hook | 🔨 Need enhancement |

### What We Need to Build

1. **Programmatic terminal spawning** - Launch PowerShell windows with pre-set environment variables
2. **Auto-registration on spawn** - New terminals register themselves immediately
3. **UI panel for teammates** - Visual dashboard showing all active agents
4. **Window focusing** - Click to bring teammate window to front
5. **Spawn MCP tool** - `spawn_teammate` tool for lead agents

## Implementation Phases

### Phase 1: Terminal Spawning (Core Infrastructure)

**Goal:** Programmatically spawn PowerShell windows with Claude Code

**Components:**
- `TerminalSpawner.cs` service
- Pre-set environment variables ($env:MULTITERMINAL_NAME, $env:MULTITERMINAL_DOC_ID)
- Process management
- Return tracking ID

**Key code pattern:**
```csharp
var psi = new ProcessStartInfo
{
    FileName = "pwsh.exe",
    Arguments = $"-NoExit -Command \"" +
               $"$env:MULTITERMINAL_NAME='{agentName}'; " +
               $"$env:MULTITERMINAL_DOC_ID='{docId}'; " +
               $"cd '{workingDir}'; " +
               $"claude\"",
    UseShellExecute = true,
    CreateNoWindow = false
};
```

**Deliverable:** Can spawn new PowerShell window running Claude Code

### Phase 2: Auto-Registration

**Goal:** Spawned terminals automatically join the team

**Components:**
- Enhanced startup hook (already exists, needs modification)
- Detection of auto-spawn environment variables
- Immediate registration call
- Notification to spawner

**Key enhancement:**
```powershell
if ($env:MULTITERMINAL_NAME -and $env:MULTITERMINAL_DOC_ID) {
    # Auto-spawned terminal - register immediately
    mcp__multiterminal__register_terminal -name $env:MULTITERMINAL_NAME -doc_id $env:MULTITERMINAL_DOC_ID
    Write-Host "🤖 Spawned as $env:MULTITERMINAL_NAME" -ForegroundColor Cyan
}
```

**Deliverable:** Spawned terminals appear in team roster automatically

### Phase 3: Spawn MCP Tool

**Goal:** Lead agents can spawn teammates via MCP tool

**Components:**
- `SpawnTeammateTool.cs` MCP tool handler
- Parameters: agent_name, agent_type, working_dir, prompt
- Integration with TerminalSpawner
- Wait for registration confirmation
- Send initial prompt to spawned agent

**API:**
```
spawn_teammate(
    agent_name: "Alice",
    agent_type: "researcher",
    prompt: "Research authentication patterns"
)
```

**Deliverable:** Lead can spawn teammates programmatically

### Phase 4: Visual Monitoring Panel

**Goal:** UI dashboard showing all active teammates

**Components:**
- New panel in MainForm (like Activity Panel)
- Real-time status updates
- "Focus Window" button per teammate
- "Send Message" quick action
- Current activity display

**UI mockup:**
```
┌─────────────────────────────────────────┐
│ 🤖 Active Teammates (3)                 │
├─────────────────────────────────────────┤
│ ✅ Alice (researcher)    [Focus] [Msg]  │
│    Currently: Exploring auth module     │
│                                          │
│ ✅ Bob (implementer)     [Focus] [Msg]  │
│    Currently: Writing tests             │
└─────────────────────────────────────────┘
```

**Deliverable:** Visual monitoring of all teammates

### Phase 5: Window Management

**Goal:** Click to focus any teammate's PowerShell window

**Components:**
- Win32 API integration (SetForegroundWindow)
- Process tracking (track HWND for each spawned terminal)
- Focus button handler
- Fallback for minimized/closed windows

**Key API:**
```csharp
[DllImport("user32.dll")]
static extern bool SetForegroundWindow(IntPtr hWnd);
```

**Deliverable:** Click [Focus] brings teammate window to front

### Phase 6: Native Bridge (Optional Enhancement)

**Goal:** Detect native agent teams and show them in MultiTerminal UI

**Components:**
- Monitor `~/.claude/teams/` directory
- Detect native team spawns
- Register external teammates in UI
- Unified dashboard for both systems

**Result:** Hybrid system - works with both native teams and MultiTerminal

## Technical Details

### Environment Variable Strategy

When spawning a teammate, set these before launching Claude:
```powershell
$env:MULTITERMINAL_NAME = "Alice"
$env:MULTITERMINAL_DOC_ID = "abc12345"
$env:MULTITERMINAL_ROLE = "researcher"  # Optional
```

The startup hook detects these and auto-registers.

### Process Tracking

Track spawned processes:
```csharp
public class SpawnedTeammate
{
    public string DocId { get; set; }
    public string Name { get; set; }
    public string AgentType { get; set; }
    public Process Process { get; set; }
    public DateTime SpawnedAt { get; set; }
    public bool IsRegistered { get; set; }
}
```

### Registration Confirmation

Wait for registration after spawn:
```csharp
await WaitForRegistration(docId, timeout: 30000);
```

Check MessageBroker for the registration event.

### Initial Prompt Delivery

After spawn + registration, send the initial prompt:
```csharp
await _broker.SendMessageAsync(
    from: "team-lead",
    to: agentName,
    message: spawnPrompt
);
```

## Advantages Over tmux

1. **Native Windows** - No Unix emulation required
2. **True window management** - Windows Terminal tabs or separate windows
3. **UI integration** - Visual dashboard showing all teammates
4. **Click to focus** - No keyboard shortcuts needed
5. **Message UI** - Send messages via buttons, not terminal commands
6. **Persistence** - Task state in database, survives closes
7. **Already integrated** - Works with existing kanban board

## User Experience Flow

**Step 1: Lead spawns team**
```
User: "I need help with this. Spawn Alice (researcher) and Bob (implementer)"
Lead: *calls spawn_teammate for each*
```

**Step 2: PowerShell windows appear**
- New window for Alice opens, Claude starts, auto-registers
- New window for Bob opens, Claude starts, auto-registers
- Both appear in UI panel

**Step 3: Visual monitoring**
- User sees Activity Panel with both teammates
- Status updates in real-time
- Click [Focus] to jump to any window
- Click [Message] to send instructions

**Step 4: Coordination**
- Teammates message each other via existing MessageBroker
- Shared task list via TaskDatabase
- UI shows who's working on what

**Step 5: Shutdown**
- Lead requests shutdown for each teammate
- Teammates approve and exit
- UI panel clears

## Success Metrics

**Technical:**
- ✅ Can spawn PowerShell windows programmatically
- ✅ Spawned terminals auto-register within 5 seconds
- ✅ UI panel shows all active teammates
- ✅ Focus button brings window to front 100% of time
- ✅ No registration race conditions

**User Experience:**
- ✅ Feels as seamless as tmux split-panes
- ✅ User can see all teammates working simultaneously
- ✅ One-click interaction with any teammate
- ✅ No manual registration needed

**Performance:**
- ✅ Spawn time < 10 seconds per teammate
- ✅ Registration confirmation < 5 seconds
- ✅ UI updates in real-time (< 1 second lag)

## Known Challenges

### Challenge 1: Process Lifetime Management
**Problem:** What if spawned window is closed manually?
**Solution:** Track process state, mark as "disconnected" in UI

### Challenge 2: Registration Race Conditions
**Problem:** Spawn and registration happen asynchronously
**Solution:** Use timeout + polling, fail gracefully

### Challenge 3: Window Focus on Multi-Monitor
**Problem:** SetForegroundWindow may fail with UAC or multi-monitor
**Solution:** Fallback to showing window location, use Windows notification

### Challenge 4: Environment Variable Persistence
**Problem:** Environment variables don't persist across sessions
**Solution:** Only used for initial spawn, registration uses MessageBroker after

## Future Enhancements

### Enhancement 1: Teammate Templates
Pre-configured agent types:
- Researcher (read-only tools)
- Implementer (full tools)
- Reviewer (read + comment)
- Tester (read + Bash)

### Enhancement 2: Spawn from UI
Button in MainForm: "Spawn New Teammate"
- Shows dialog for name, type, prompt
- Spawns immediately
- No need to ask lead agent

### Enhancement 3: Workspace Management
Save/restore team configurations:
- "Load my research team" (3 researchers)
- "Load my dev team" (frontend, backend, tester)

### Enhancement 4: Native Team Detection
Automatically detect when user uses native agent teams:
- Watch `~/.claude/teams/` directory
- Show native teammates in UI alongside MultiTerminal ones
- True hybrid system

## References

### Documentation
- [Orchestrate teams of Claude Code sessions](https://code.claude.com/docs/en/agent-teams)
- [Create custom subagents](https://code.claude.com/docs/en/sub-agents)
- [How to Set Up Claude Code Agent Teams](https://darasoba.medium.com/how-to-set-up-and-use-claude-code-agent-teams-and-actually-get-great-results-9a34f8648f6d)
- [Claude Code Agent Teams Setup Guide](https://www.marc0.dev/en/blog/claude-code-agent-teams-multiple-ai-agents-working-in-parallel-setup-guide-1770317684454)

### Community Resources
- [Claude Code's Hidden Multi-Agent System](https://paddo.dev/blog/claude-code-hidden-swarm/)
- [Claude Code Swarm Orchestration](https://gist.github.com/kieranklaassen/4f2aba89594a4aea4ad64d753984b2ea)

### Memory References
- Pattern Success #8: Hook Investigation (Bob + Charlie, 2026-02-06)
- Native vs MCP system-level distinction discovery
- Validation of Leader pattern from Alice + Diana formula

## Conclusion

MultiTerminal has the unique opportunity to become **the standard solution for Claude agent teams on Windows**. We have 80% of the infrastructure already built - we just need to add programmatic spawning and polish the UX.

This positions MultiTerminal as:
- Native Windows alternative to tmux
- Production-ready today (not experimental like native teams)
- Integrated with kanban board and task management
- Visual monitoring superior to terminal-only solutions

**Let's build it!** 🚀
