# MultiTerminal Startup Hang - RESOLVED ✅

**Status**: FIXED (2026-02-08)
**Root Cause**: Infinite loop in `ProjectService.SkipJsonValue()` custom JSON parser
**Resolution**: Fixed position increment logic in nested object/array skipping

---

## Problem Summary
MultiTerminal hangs on startup after user tested Claude Code's native Teams feature. The hang persists even after closing the Teams session.

## Root Cause Analysis (RESOLVED)

### What We Discovered:
1. **Initial Symptoms**: After testing Claude Code Teams (spawning external PowerShell), MultiTerminal started hanging on startup
2. **Not the Teams config**: Renaming `.claude/teams` directory didn't fix it
3. **Not Claude Code hooks**: Hooks don't run until Claude Code is launched inside MultiTerminal

### Hang Location (Narrowed Down):
Through extensive debug tracing, we tracked the hang through:
- ✅ Constructor completes successfully
- ✅ `InitializeMcpServerAndChatPanel` completes
- ✅ Form.Shown event fires
- ✅ `RestoreSession()` completes successfully
- ✅ Calls `AddNewTerminal()` to create default terminal
- ❓ **HANGS SOMEWHERE IN AddNewTerminal** - exact location TBD

### Key Debug Findings:

1. **DockPanel Deserialization Issue**:
   - Setting `WindowState = Maximized` triggers DockPanel to deserialize
   - Uses reflection to create NEW instances of ProjectService and SessionDatabase
   - Stack trace shows: `RuntimeMethodHandle.InvokeMethod` → dynamic instantiation

2. **Current Debug Output Locations**:
   ```
   [RestoreSession] Completed
   [AddNewTerminal] Creating TerminalDocument...
   [AddNewTerminal] TerminalDocument created
   [AddNewTerminal] Pre-registering terminal with MCP server...
   [AddNewTerminal] Terminal registered as: Unassigned
   [AddNewTerminal] Showing terminal in DockPanel (mode: Tab)...
   ??? LIKELY HANGS HERE - either in doc.Show() or doc.StartTerminal() ???
   ```

## Files Modified (Debug Code Added)

### MainForm.cs
- Added stack trace logging to ProjectService constructor (shows reflection calls)
- Added debug output to:
  - Constructor completion
  - Shown event handlers (2 handlers - one for WindowState, one for RestoreSession)
  - RestoreSession method (complete flow)
  - AddNewTerminal method (step-by-step)

### ProjectPanelDocument.cs
- Modified to accept injected SessionDatabase (prevents duplicate creation)
- Added `SetSessionDatabase()` method

### Services/ProjectService.cs
- Added stack trace logging to constructor to track who creates instances

### Services/SessionIndexingService.cs
- Added debug output to constructor
- Fixed potential deadlock in FindNodePath (timeout handling)

### Services/SessionDatabase.cs
- Added debug output to Initialize() method

## Current State

### What Works:
- Constructor completes ✅
- MCP server initializes ✅
- Form shows ✅
- RestoreSession completes ✅
- AddNewTerminal starts ✅

### What Hangs:
- **Most likely**: `doc.Show(_dockPanel, DockState.Document)` at line ~1036
  - This triggers WebView2 initialization for the terminal
  - WebView2 might be waiting for something (network, process, etc.)
- **Alternative**: `doc.StartTerminal(dir, terminalName, autoRunCommand)` at line ~1041
  - This spawns PowerShell process
  - Could be waiting for process spawn or initialization

## Next Steps (FOR NEXT SESSION)

1. **Get the latest debug log** showing where AddNewTerminal hangs
   - User will run MultiTerminal and provide last debug lines
   - This will show if it's doc.Show() or doc.StartTerminal()

2. **If doc.Show() hangs** (WebView2 initialization):
   - Check WebView2 installation/corruption
   - Try clearing WebView2 cache: `%LOCALAPPDATA%\MultiTerminal\WebView2Data`
   - Consider async initialization pattern

3. **If doc.StartTerminal() hangs** (PowerShell spawn):
   - Check if PowerShell is accessible
   - Test PowerShell spawn outside MultiTerminal
   - Check for process deadlocks or environment issues

4. **Temporary Workaround** (if needed):
   - Skip terminal auto-creation on startup
   - Let user manually create terminal after app loads
   - This isolates the UI from the terminal initialization hang

## Key Files to Reference

- `H:\DevLaptop\ClarionPowerShell\MultiTerminal\MainForm.cs` (line 988+ for AddNewTerminal)
- `H:\DevLaptop\ClarionPowerShell\MultiTerminal\Docking\TerminalDocument.cs` (terminal initialization)
- Deploy location: `H:\DevLaptop\ClarionPowerShell\Deploy\MultiTerminal.exe`

## Debug Scripts Available

- `check-claude-config.ps1` - Checks Claude Code configuration
- `check-teams.ps1` - Checks Teams configuration files
- `diagnose-startup.ps1` - Diagnose startup issues
- `disable-teams-temp.ps1` - Temporarily disable Teams config
- `fix-layout.ps1` - Fix corrupted layout
- `fix-large-database.ps1` - Backup large session database
- `checkpoint-databases.ps1` - Checkpoint SQLite WAL files
- `deploy.ps1` - Deploy script
- `verify-deploy.ps1` - Verify deployment

## Important Context

- User's sessions.db was 796 MB (backed up to sessions.db.backup-*)
- layout.xml was deleted (doesn't exist)
- Project panel auto-show is disabled (commented out)
- Teams directory renamed to teams.backup
- 4 pending terminal sessions in settings

## Questions for User (Next Session)

1. What are the LAST debug lines before the hang?
2. Does Task Manager show PowerShell processes spawning?
3. Can you open PowerShell manually outside MultiTerminal?
4. Any antivirus or security software that might block process spawning?

## Success Criteria

MultiTerminal should:
1. Start up without hanging
2. Display the main window
3. Create at least one default terminal
4. Allow user to interact with the terminal

---

## RESOLUTION (2026-02-08) ✅

### The Actual Root Cause

Through systematic debug logging, we discovered the hang was NOT in AddNewTerminal, WebView2, or PowerShell spawning. The hang was in **`ProjectService.ParseProjectJson()`** - specifically in the **`SkipJsonValue()`** method.

**The Bug:**
```csharp
// BEFORE (BUGGY):
while (pos < json.Length && depth > 0)
{
    if (json[pos] == '{') depth++;        // ❌ Forgot pos++
    else if (json[pos] == '}') depth--;   // ❌ Forgot pos++
    else if (json[pos] == '"')
        ParseJsonString(json, ref pos);
    else
        pos++;
}
```

When `SkipJsonValue()` encountered `{` or `}` brackets while skipping nested structures, it:
1. ✅ Correctly updated the depth counter
2. ❌ **FORGOT to increment `pos`**
3. 💥 Got stuck in infinite loop at position 314 (inside hooks array)

**The Trigger:**
The `.claude/project.json` file contained a `"hooks"` property with nested objects and arrays. When the parser tried to skip this unknown property, it hit the bug and looped 167,682+ times at position 314.

**The Fix:**
```csharp
// AFTER (FIXED):
while (pos < json.Length && depth > 0)
{
    if (json[pos] == '{')
    {
        depth++;
        pos++;  // ✅ Now increments
    }
    else if (json[pos] == '}')
    {
        depth--;
        pos++;  // ✅ Now increments
    }
    else if (json[pos] == '"')
    {
        ParseJsonString(json, ref pos);
    }
    else
    {
        pos++;
    }
}
```

Applied the same fix to array handling (`[` and `]` brackets).

### Files Modified (Bug Fix)

**Services/ProjectService.cs:**
- Fixed `SkipJsonValue()` method to increment `pos` for all bracket types
- Line ~1127-1133 (object handling)
- Line ~1147-1153 (array handling)

### Verification

- Clean build: ✅ 0 warnings, 0 errors
- Startup test: ✅ No hang, launches normally
- Debug logging: ✅ Preserved for future troubleshooting

### Key Learnings

1. **Systematic debug logging works** - Progressive narrowing through 500K+ log lines found the exact position
2. **Custom parsers are risky** - This bug existed since the parser was written but only triggered with complex nested JSON
3. **User intuition was correct** - Suspected the JSON file content was the trigger (it was, but the parser was guilty)

### Deployment

- Last successful deployment: 2026-02-08 12:53:36
- Location: `H:\DevLaptop\ClarionPowerShell\Deploy\MultiTerminal.exe`
- Status: **WORKING** ✅
