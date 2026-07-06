# MultiTerminal Agent Rules

These rules are injected into every terminal session at startup. They apply to ALL agents working in MultiTerminal, regardless of project.

---

## Task Workflow (MANDATORY)

1. **ALWAYS create a kanban task** for any work requiring research, planning, or coding. No exceptions, even for simple things.
2. After creating a task, ask the user: "Should I make it active and start working on it?"
3. If the user assigns you a task, ask: "Want me to make it active?"
4. Always **claim** tasks you're working on. Never work unclaimed.
5. Use `/kanban-task` before starting work on any ticket — it detects state and guides the workflow.
6. Never skip the lifecycle: claim -> plan -> checklist -> coding -> testing.
7. Never mark checklist items as "done" — only the user (PM/tester) moves testing -> done.
8. Write continuation notes after every checklist transition.
9. Use `set_task_active` when starting work on a task.
10. Never create "build and test" checklist items — that's what the testing column is for.
11. Call `link_task_file` for every file modified/created during task work so reviewers can trace changes.

## Testing Protocol

Interactive pass/fail testing via checklist items:
1. Set checklist items to `testing`, present ONE at a time.
2. User replies **pass** or **fail** (with details if fail).
3. Pass -> done. Fail -> coding with failure reason in notes.
4. Complete ALL testing before ANY coding fixes.
5. Fix all failed items (use subagents for parallel fixes), then re-test.

## Session Continuity

- At session start, check the kanban board for your active task. The board is the source of truth.
- When user asks "what did you do last session," look up YOUR OWN agent name, not the global latest session.
- ACTIVE-CONTEXT.md is auto-maintained by hooks. Use it as context but verify against the board.

## Communication

- When a message arrives from ClaudeRemote (MultiRemote), always reply via `mcp__multiterminal-channel__reply` so it appears on John's phone.
- When you see `[cm]` as user input, immediately check your messages via `get_messages`.
- Checklist transition notes appear as inbox notifications — keep to 1-2 sentences, no markdown.

## Build & Deploy

- Running app is at `H:\DevLaptop\ClarionPowerShell\Deploy\MultiTerminal.exe`.
- We are running what we're working on — Deploy folder has the live binary.
- You CAN run `mcp__windows-build-runner__build_project` to compile. You CANNOT copy to Deploy.
- Deploy workflow (John only): exit app -> run `deploy.ps1` -> relaunch.
- After a successful build, tell John it's ready and he needs to deploy.

## Code Search

Use `mcp__multiterminal__search_code` (ripgrep) for content search, NOT the built-in Grep tool. This applies to all agents and subagents.

## Windows Bash

- NEVER use `$env:VARNAME` or `%VARNAME%` in Bash tool — they get mangled.
- Hardcode known paths: `APPDATA` = `C:\Users\John Hickey\AppData\Roaming`.
- Prefer Glob/Grep/Read over PowerShell for file operations.

## Terminology

| User Says | System | Tool |
|-----------|--------|------|
| "Create a ticket/task" | Kanban (visible in UI) | `mcp__multiterminal__create_task` |
| "Check the board" | Kanban | `mcp__multiterminal__list_tasks` |
| "Add to YOUR tasks" | Internal (only you see) | `TaskCreate` |

Default: "create a task" -> use kanban tickets.

## Development Principles

1. "Don't rebuild what exists" — explore existing code before new implementations.
2. "Explore before coding" — check Services/, Models/, Database first.
3. Parallel > Sequential — divide work by strengths, execute simultaneously.

## Agent Naming

| System | Format | Example | Use For |
|--------|--------|---------|---------|
| MultiTerminal | Plain name | Alice, Diana | Interactive work |
| Native Teams | "Agent " prefix | Agent Alice | Coding sprints (AgentPanel) |

## Team Agents

- Must `TeamCreate` first before spawning teammates.
- Use Sonnet minimum — Haiku agents are unreliable for team messaging.
- Use simple names to avoid misrouting.
- Can only lead one team at a time.

## Browser Tabs (HUD)

- `open_browser_tab(terminalId, title, url|content)` — display content in terminal HUD.
- Use for docs, HTML previews, dashboards, rendered output.

## License

`THIRD-PARTY-NOTICES.md` in repo root must ship with any distribution. Update when adding dependencies.
