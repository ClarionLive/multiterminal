# Terminal Management

> Terminal spawning, ConPty lifecycle, agent process coordination, and companion process management.

**Tags:** `terminal`, `process`, `conpty`

## Key Files

- `Controls/TerminalControl.cs` (1096 LOC)
  - Complete terminal emulator control combining ConPtyTerminal (shell backend) with WebViewTerminalRenderer (xterm.js frontend), providing input injection, chunked text delivery, and Claude Code startup detection.
- `Terminal/ConPtyTerminal.cs` (905 LOC)
  - Windows Pseudo Console (ConPTY) manager for true terminal emulation; creates pipes, pseudo-console, spawns shell process with VT-100 support.
- `Services/TerminalSpawner.cs` (710 LOC)
  - Process launcher for PowerShell terminals running Claude Code with env var injection
- `Services/LaunchCommandBuilder.cs` (642 LOC)
  - Builds Claude Code CLI launch commands with MT config flags from dynamic assembly location
- `Services/AgentProcess.cs` (512 LOC)
  - Manages headless Claude Code processes with piped stdin/stdout for agent spawning
- `Services/CompanionProcessManager.cs` (266 LOC)
- `MCPServer/Models/TerminalInfo.cs` (68 LOC)
  - Terminal registration info with identity, color, doc ID, and spawn handshake ready flag.

## Key Classes

- **TerminalSpawner** (class) — `Services/TerminalSpawner.cs:15`
- **AgentProcess** (class) — `Services/AgentProcess.cs:21`
- **CompanionProcessManager** (class) — `Services/CompanionProcessManager.cs:17`
- **CompanionStatus** (class) — `Services/CompanionProcessManager.cs:257`
- **LaunchCommandBuilder** (class) — `Services/LaunchCommandBuilder.cs:13`
- **LaunchCommand** (class) — `Services/LaunchCommandBuilder.cs:170`
- **ConPtyTerminal** (class) — `Terminal/ConPtyTerminal.cs:16`
- **TerminalTitleChangedEventArgs** (class) — `Controls/TerminalControl.cs:15`
- **TerminalControl** (class) — `Controls/TerminalControl.cs:25`
- **TerminalInfo** (class) — `MCPServer/Models/TerminalInfo.cs:8`
- **RegisterResult** (class) — `MCPServer/Models/TerminalInfo.cs:62`

## Key Methods

- `TerminalSpawner.SpawnTerminal` — `Services/TerminalSpawner.cs:188`
- `TerminalSpawner.MarkAsRegistered` — `Services/TerminalSpawner.cs:308`
- `TerminalSpawner.GetTeammate` — `Services/TerminalSpawner.cs:323`
- `TerminalSpawner.GetTeammateByName` — `Services/TerminalSpawner.cs:334`
- `TerminalSpawner.GetAllTeammates` — `Services/TerminalSpawner.cs:346`
- `TerminalSpawner.WaitForRegistration` — `Services/TerminalSpawner.cs:360`
- `TerminalSpawner.RemoveTeammate` — `Services/TerminalSpawner.cs:381`
- `TerminalSpawner.IsProcessRunning` — `Services/TerminalSpawner.cs:392`
- `TerminalSpawner.GetActiveCount` — `Services/TerminalSpawner.cs:411`
- `AgentProcess.SpawnAsync` — `Services/AgentProcess.cs:100`
- `AgentProcess.SendMessageAsync` — `Services/AgentProcess.cs:204`
- `AgentProcess.InterruptAsync` — `Services/AgentProcess.cs:245`
- `AgentProcess.StopAsync` — `Services/AgentProcess.cs:270`
- `CompanionProcessManager.LoadConfig` — `Services/CompanionProcessManager.cs:52`
- `CompanionProcessManager.StartAll` — `Services/CompanionProcessManager.cs:79`
- `CompanionProcessManager.GetStatus` — `Services/CompanionProcessManager.cs:163`
- `CompanionProcessManager.StopAll` — `Services/CompanionProcessManager.cs:218`
- `LaunchCommandBuilder.BuildClaudeCommand` — `Services/LaunchCommandBuilder.cs:23`
- `LaunchCommandBuilder.GetMtPluginPath` — `Services/LaunchCommandBuilder.cs:82`
- `LaunchCommandBuilder.GetMcpConfigPath` — `Services/LaunchCommandBuilder.cs:94`
- `LaunchCommandBuilder.GetMtSourcePath` — `Services/LaunchCommandBuilder.cs:125`
- `ConPtyTerminal.Start` — `Terminal/ConPtyTerminal.cs:87`
- `ConPtyTerminal.Write` — `Terminal/ConPtyTerminal.cs:158`
- `ConPtyTerminal.Write` — `Terminal/ConPtyTerminal.cs:177`
- `ConPtyTerminal.SendKey` — `Terminal/ConPtyTerminal.cs:185`

## External Callers

> Code outside this subsystem that calls into it.

- `MainForm.OnSpawnAgentRequested` — `MainForm.cs:1170`
- `Program.Main` — `AgentProcessTest/Program.cs:45`

## Gotchas

- Single-quotes in env vars must be escaped
- Process.Start(UseShellExecute=true) opens new window
- WaitForRegistration polls every 500ms
- Handles --include-partial-messages mode where message objects are re-emitted with accumulated content blocks; tracks message IDs and content counts to deduplicate
- Walks up from assembly location for .claude/CLAUDE.md marker; single quotes doubled for PowerShell safety; --mcp-config omitted (auto-discovered via .mcp.json in project root)
- Critical: closing pipe ends in correct order prevents deadlocks; UTF-8 remainder buffer handles split multi-byte sequences; process wait thread crucial for EOF detection.
- MaxChunkSize=500 bytes to avoid PowerShell paste mode (triggers at ~500-600 chars). InjectInputAsync writes text then sends Enter via xterm.js — must use JS Enter not raw bytes for Claude Code compatibility. ClaudeCodeDetected fires only once per session.
- IsReady flag indicates agent sent ready confirmation via webhook or message, DocId links to UI panel, Color for chat rendering

---
_Generated 2026-06-10T17:01:21.3576725Z · [Back to index](./index.md)_
