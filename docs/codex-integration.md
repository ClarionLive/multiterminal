# Codex CLI Terminal Integration — Phase 1

MultiTerminal Phase 1 makes [OpenAI Codex CLI](https://github.com/openai/codex) a first-class terminal type alongside Claude Code. A Codex terminal launched from MultiTerminal registers itself as a team member, reads its inbox, and can participate in messaging and kanban workflows via MCP — same as a Claude Code terminal, minus the hook pipeline and skills.

Related kanban tasks:
- `cf4575e7` — Phase 1 MVP (this doc)
- `c6091fb8` — Phase 2 (hooks + session tracking + full parity)

---

## What works in Phase 1

| Capability | Status | Notes |
|-----------|--------|-------|
| MCP server (`multiterminal`) | ✅ | Same command as Claude Code terminals. Registered via `~/.codex/config.toml`. |
| MCP server (`mcp-gateway`) | ✅ | Also registered via config.toml. |
| Env-var identity (`MULTITERMINAL_NAME`, `MULTITERMINAL_DOC_ID`, `MULTITERMINAL_PROJECT_ID`) | ✅ | Injected by `ConPtyTerminal.StartProcess` into the parent PowerShell; Codex inherits. |
| Startup prompt (tells Codex its name + first actions) | ✅ | Template at `%APPDATA%\multiterminal\codex\startup-prompt.md`; delivered by `codex-launch.ps1` as the initial user message. |
| `AGENTS.md` at project root | ✅ | Generated on first Codex launch. Translates `.claude/CLAUDE.md` when present, else writes a stub. Write-once. |
| Per-project default terminal | ✅ | `defaultTerminal` field in `.claude/project.json` and the `default_terminal` column in SQLite. Asked in the New Project wizard; editable in the Project pane settings form. |
| Project card split-button launch | ✅ | Main click launches the default; dropdown offers Claude Code / Codex as one-off overrides. "Terminal: X" label shows the default. |
| Settings UI (Codex binary path, model, effort, default agent name) | ✅ partial | Binary path + default agent name are applied on launch. Model / effort are persisted but not yet written to `config.toml` (Phase 2). |

## What's not in Phase 1 (see `c6091fb8`)

| Capability | Status | Notes |
|-----------|--------|-------|
| Hook pipeline (SessionStart, Stop, PreToolUse, etc.) | ❌ | Codex hook event schema differs from Claude Code. Needs a shim. |
| Session JSONL import | ❌ | Codex writes `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` with a different schema. Needs an importer. |
| Skills (`/skillname` triggers) | ❌ | Codex has no Skill tool. Phase 2 will expose an MCP `invoke_skill` tool instead. |
| `register_session` from the startup prompt | ❌ | Omitted until the JSONL importer lands — without it, the session ID can't be tied back to transcripts. |
| Model / effort written to `~/.codex/config.toml` | ❌ | Settings persist but don't yet flow into TOML. Avoids the "managed block top-level key spills into adjacent tables" footgun (see "Known limitations"). |

Explicitly NOT planned:
- Plugin marketplace system (Claude-Code-idiomatic, doesn't map).
- `Task` subagent tool with `subagent_type`.
- `PreToolUse permissionDecision: ask/allow/deny` semantics.

---

## How to use

### Create a Codex-first project

1. **File → New Project** in MultiTerminal.
2. Fill in name, folder, team lead as usual.
3. Set **Default Terminal** to `Codex`.
4. Click *Create & Launch*. A new terminal opens and runs `codex`.

### Switch an existing project to Codex

1. Open the project in the Project pane.
2. In the info section, change **Default Terminal** to `Codex`.
3. Click the main launch button → it now launches Codex.

### One-off override

Click the ▼ arrow next to **Launch in Terminal**, pick Claude Code or Codex. The project's stored default is not changed.

### Point at a custom codex binary

**Settings → Codex tab → Binary path**. Blank resolves `codex` via PATH.

---

## Architecture

```
Project.DefaultTerminal (string: "claude-code" | "codex")
  ├── Models/Project.cs (C# model, default "claude-code")
  ├── Models/TerminalKind.cs — enum + TerminalKindHelper (parse/serialize/normalize)
  ├── .claude/project.json "defaultTerminal" key
  └── projects.default_terminal column (NOT NULL DEFAULT 'claude-code')

Launch path
  ├── LaunchCommandBuilder.BuildCommand(kind, project)
  │     ├── kind == ClaudeCode → BuildClaudeCommand (flags, --mcp-config, plugin-dir)
  │     └── kind == Codex     → BuildCodexCommand
  │           ├── CodexConfigService.EnsureMcpRegistration() — writes ~/.codex/config.toml
  │           ├── CodexPromptService.EnsureStartupFiles() — startup-prompt.md + codex-launch.ps1
  │           ├── CodexAgentsService.EnsureAgentsMd(projectRoot) — AGENTS.md from CLAUDE.md
  │           └── autoRun = "& codex-launch.ps1; exit" (sets MULTITERMINAL_CODEX_BIN first)
  └── ConPtyTerminal.StartProcess — injects MT env vars into the parent PowerShell

UI (Project pane split button)
  ├── panel.html .launch-split (main + arrow) + dropdown menu + "Terminal: X" label
  ├── ProjectPanelRenderer.OnWebMessageReceived parses optional "terminal" field
  ├── LaunchRequestedEventArgs (override flows through)
  ├── ProjectPanelDocument.OnRendererLaunchRequested
  └── MainForm.OnProjectLaunchRequested
        ├── kind = override ?? project.DefaultTerminal ?? ClaudeCode
        ├── LaunchCommandBuilder.BuildCommand(kind, project)
        └── AddNewTerminal(...)  -- no dialog; direct launch
```

### File layout

| Path | Purpose |
|------|---------|
| `Models/TerminalKind.cs` | Enum + `TerminalKindHelper` (string<->enum conversion). |
| `Services/CodexConfigService.cs` | Idempotent `~/.codex/config.toml` manager (marker-delimited block). |
| `Services/CodexPromptService.cs` | Writes `startup-prompt.md` + `codex-launch.ps1` to `%APPDATA%\multiterminal\codex\`. |
| `Services/CodexAgentsService.cs` | Writes `AGENTS.md` at project root. |
| `Services/LaunchCommandBuilder.cs` | Adds `BuildCodexCommand` + `BuildCommand(kind, project)` dispatcher. |
| `ProjectPanel/panel.html` | Split button + Terminal: label + Default Terminal field in info form. |
| `Dialogs/NewProjectWpfDialog.xaml[.cs]` | Default Terminal dropdown in the New Project wizard. |
| `Dialogs/SettingsWpfDialog.xaml[.cs]` | Codex tab (binary path, model, effort, default agent name). |

### Managed-content markers

Files MultiTerminal owns but the user can customize are marked so regeneration doesn't stomp edits:

| File | Rule |
|------|------|
| `~/.codex/config.toml` | Content between the `# >>> MULTITERMINAL MANAGED` markers is replaced on every Codex launch. Outside the markers is preserved. First mutation of an existing file creates a `.multiterminal.bak`. |
| `%APPDATA%\multiterminal\codex\startup-prompt.md` | Write-once. Delete the file to get a fresh default. |
| `%APPDATA%\multiterminal\codex\codex-launch.ps1` | Write-once. Delete to refresh. |
| `{projectRoot}\AGENTS.md` | Write-once. Delete + relaunch to regenerate from `.claude/CLAUDE.md`. |

---

## Known limitations

- **Codex binary resolution:** if the path is missing (not in PATH and no Settings override), the spawned PowerShell prints `codex not recognized` and exits. Same failure mode as a missing `claude`.
- **Model / effort settings don't flow to `config.toml` yet.** Persisting them took Phase 1 time; wiring them in requires splitting the managed TOML block so top-level keys don't spill into `[mcp_servers.*]` table context. Phase 2 concern.
- **Managed-content updates after an upgrade.** The write-once files (`startup-prompt.md`, `codex-launch.ps1`, `AGENTS.md`) don't auto-refresh when MT ships a new default template. Users must delete and relaunch. A version-marker check is a future polish.
- **`register_session` omitted.** The startup prompt instructs Codex to call `register_terminal` but not `register_session`, because without the JSONL importer (Phase 2) the session ID can't be tied back to transcripts.
- **The Project pane launch button no longer shows the old "Replace Current Terminal / Claude Command" dialog.** Split-button click launches directly. `ShowProjectTerminalDialog` is dead code — left in place for potential future reuse.

---

## Testing

Smoke test (run after a successful build):

1. Build: `dotnet build MultiTerminal.csproj -c Debug --no-restore` (0 errors/warnings expected).
2. Launch MultiTerminal.
3. Create a new project; pick **Default Terminal = Codex** in the wizard. Confirm the terminal that opens runs `codex` (not `claude`).
4. Check `~/.codex/config.toml` — should contain a `# >>> MULTITERMINAL MANAGED` block with `[mcp_servers.multiterminal]` and `[mcp_servers.mcp-gateway]`.
5. Check the project root — `AGENTS.md` should exist with either CLAUDE.md content or a stub.
6. In the Codex terminal, Codex should have called `register_terminal` on startup (it reports "Registered as <Name>"). Verify in the team roster panel that the new terminal appears within 10 seconds.
7. From another MT terminal, send a message to the Codex terminal via `send_message`. Codex should receive it in its inbox.
8. Have Codex claim a kanban task via `claim_task`. Verify the board reflects the claim.
9. Open the Project pane for the same project. Click the **▼** arrow on the launch button, pick **Claude Code**. Verify a Claude terminal opens for that project (override works without changing the default).
10. Change **Default Terminal** in the info form back to Claude Code. Close and reopen the pane — the dropdown should reflect the saved value, and the main launch button should launch Claude by default.

---

## Phase 2 / follow-ups

Tracked in task `c6091fb8`:
- Codex hook pipeline (Codex hook schema → MultiTerminal events shim)
- Codex session JSONL importer (rollout file → `session_messages` table)
- MCP `invoke_skill` tool (replaces Claude's Skill tool for Codex agents)
- Codex model / effort persistence → `~/.codex/config.toml` top-level keys (needs managed-block restructuring)
- Remove dead code: `ShowProjectTerminalDialog` in `MainForm.cs`
- Version-marker check for write-once files so MT upgrades auto-refresh templates
