# Native Teams Phase 2 - Session Continuation

## Summary
Successfully debugged and fixed grid cell spawning for Native Teams. Alice can now spawn with MCP server access!

## What We Accomplished

### ✅ Fixed Critical Issues

1. **Window Shift Bug (Multi-Monitor)**
   - **Problem:** Maximized window on secondary monitor shifted left/right on focus changes
   - **Fix:** `MainForm.cs` line 826-851 - Skip manual bounds when restoring maximized windows
   - **Result:** Window stays stable on secondary monitor

2. **Test Spawn Infrastructure**
   - **Updated:** `TestSpawnForm.cs` to spawn grid cells instead of external PowerShell windows
   - **Method:** Calls `MainForm.AddNewTerminal()` with `identityName` and `autoRunCommand`
   - **Location:** Test Spawn button in toolbar

3. **Auto-Registration Hook System**
   - **Created:** `C:\Users\John Hickey\.claude\hooks\multiterminal-registration.ps1`
   - **Purpose:** Detects `MULTITERMINAL_NAME` and `MULTITERMINAL_DOC_ID` env vars at session start
   - **Updated:** Global `settings.json` SessionStart hook to call this script
   - **Status:** Hook runs and logs to `hook-debug.log`, but output is hidden (Claude Code clears terminal)

4. **Working Directory Debug**
   - **Added:** Spawn debug logging to `spawn-debug.log`
   - **Confirmed:** PowerShell starts in correct Deploy directory
   - **Fix:** Added explicit `cd` before `claude` command in `ConPtyTerminal.cs` line 324

5. **MCP Server Connection (THE KEY FIX!)**
   - **Problem:** Spawned terminals couldn't access MultiTerminal MCP server
   - **Root Cause:** Deploy project had empty `mcpServers: {}` in `.claude.json`
   - **Solution:** Added multiterminal server config to Deploy project entry in `C:\Users\John Hickey\.claude.json`
   - **Format:**
     ```json
     "mcpServers": {
       "multiterminal": {
         "type": "http",
         "url": "http://localhost:5050/mcp",
         "headers": {
           "Authorization": "Bearer local"
         }
       }
     }
     ```

## Current Status

### ✅ Working
- Grid cell spawning via Test Spawn button
- Environment variables set correctly (MULTITERMINAL_NAME, MULTITERMINAL_DOC_ID)
- Working directory is Deploy (where .claude config exists)
- MCP server connection established
- Alice can see multiterminal in `/mcp list-servers`

### ⚠️ Needs Testing
- Auto-registration flow (Alice should now be able to call `mcp__multiterminal__register_terminal`)
- Inter-terminal messaging between spawned agents

## Architecture Notes

### Grid Cell Spawn Flow
1. User clicks Test Spawn → `TestSpawnForm` opens
2. User enters name (e.g., "Alice") → clicks Spawn
3. `MainForm.AddNewTerminal()` called with:
   - `workingDirectory`: Deploy folder
   - `identityName`: "Alice"
   - `autoRunCommand`: "claude --dangerously-skip-permissions"
4. Creates `TerminalDocument` (grid cell)
5. Pre-registers with MCP broker (name → doc_id mapping)
6. Launches PowerShell with env vars set
7. PowerShell runs: `cd Deploy; claude --dangerously-skip-permissions`
8. Claude Code starts, reads `.claude.json` from Deploy project config
9. Connects to `http://localhost:5050/mcp`
10. MCP tools available!

### Key Files Modified
- `MainForm.cs` - Window bounds fix, made AddNewTerminal public
- `TestSpawnForm.cs` - Updated to spawn grid cells with proper command
- `ConPtyTerminal.cs` - Added cd command before autoRunCommand, debug logging
- `C:\Users\John Hickey\.claude.json` - Added multiterminal MCP server to Deploy project
- `C:\Users\John Hickey\.claude\hooks\multiterminal-registration.ps1` - Auto-registration hook
- `Deploy\.claude\CLAUDE.md` - Auto-registration instructions for spawned agents

### MCP Configuration Discovery
- **Wrong:** `mcp.json` files (for stdio-based servers)
- **Correct:** `.claude.json` per-project configuration
- **Format:** HTTP servers use `"type": "http"` with URL endpoint
- **Location:** `C:\Users\John Hickey\.claude.json` → projects → Deploy → mcpServers

## Next Steps

1. **Test Auto-Registration**
   - Spawn Alice via Test Spawn
   - Verify she auto-registers (should see "I'm Alice, registered and ready")
   - Check if she can send messages to other terminals

2. **Verify Hook Output** (Optional)
   - Hook runs but output is cleared by Claude Code
   - Could modify CLAUDE.md to have agents check env vars directly instead

3. **Clean Up Debug Code** (Optional)
   - Remove spawn-debug.log logging once confirmed working
   - Remove excessive Trace.WriteLine statements in spawn flow

4. **Document Success**
   - Update MEMORY.md with Native Teams pattern
   - Record the .claude.json discovery as key learning

## Important Learnings

1. **Multi-Monitor Maximization:** Don't set manual bounds for maximized windows
2. **MCP HTTP Servers:** Config goes in `.claude.json`, NOT `mcp.json`
3. **Grid Cells vs External Windows:** Grid cells share MCP server, external don't
4. **Working Directory:** Must `cd` before running `claude` to ensure correct project context
5. **Hooks Output:** SessionStart hook output gets cleared by Claude Code startup

## Files to Reference
- Test spawn: Click "🚀 Test Spawn" button in toolbar
- Config: `C:\Users\John Hickey\.claude.json` (line 3088+)
- Hook: `C:\Users\John Hickey\.claude\hooks\multiterminal-registration.ps1`
- Deploy config: `H:\DevLaptop\ClarionPowerShell\Deploy\.claude\CLAUDE.md`
- Debug logs:
  - `C:\Users\John Hickey\.claude\hooks\hook-debug.log`
  - `C:\Users\John Hickey\.claude\hooks\spawn-debug.log`

---

**STATUS: Ready for auto-registration testing! 🎉**
