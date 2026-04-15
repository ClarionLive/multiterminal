# Claude Code Internals Research Report

**Date:** 2026-03-31
**Source:** Analysis of `instructkr/claw-code` repo (clean-room reimplementation), official docs, leaked source analysis posts, Reddit community discussion
**Purpose:** Understand Claude Code internals to improve MultiTerminal's integration

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Hooks & Dispatch](#hooks--dispatch)
3. [Context Window Management](#context-window-management)
4. [MCP Integration](#mcp-integration)
5. [Plugin System](#plugin-system)
6. [Subagent & Teams Architecture](#subagent--teams-architecture)
7. [Channels & Messaging](#channels--messaging)
8. [Permission & Security Model](#permission--security-model)
9. [Environment Variables & Feature Flags](#environment-variables--feature-flags)
10. [Actionable Recommendations for MultiTerminal](#actionable-recommendations-for-multiterminal)

---

## Executive Summary

On March 31, 2026, Claude Code v2.1.88 was published to npm with a 59.8 MB source map file that exposed the full TypeScript source (1,902 files, 512K+ lines). The `instructkr/claw-code` repo is a clean-room Python/Rust reimplementation that preserves the architecture without copying proprietary code. This research examines the internals to optimize MultiTerminal's integration.

**Key discoveries with immediate impact:**

- **CLAUDE.md budget is 12,000 chars total** — our project CLAUDE.md exceeds this and is being silently truncated
- **Hook outputs via `additionalContext` enter context without truncation** — we need to keep hook output minimal
- **Hooks from multiple plugins are additive** — explains potential double-firing issues
- **MCP tools are deferred by default** — only names loaded (~120 tokens), full schemas fetched via ToolSearch on demand
- **Compaction preserves only ~12% of tokens** — critical context must be explicitly marked for survival
- **The channel warning can be suppressed** via `--dangerously-load-development-channels` flag (already in LaunchCommandBuilder)
- **`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` breaks channels** — it kills GrowthBook flag evaluation alongside telemetry

---

## Hooks & Dispatch

*Full report: [claude-code-hooks-internals-report.md](../claude-code-hooks-internals-report.md)*

### Event Catalog (21+ events)

| Phase | Events |
|-------|--------|
| Session setup | SessionStart, InstructionsLoaded, ConfigChange |
| User input | UserPromptSubmit, CwdChanged |
| Tool lifecycle | PreToolUse, PermissionRequest, PostToolUse, PostToolUseFailure, Elicitation, ElicitationResult, FileChanged |
| Agent lifecycle | SubagentStart, SubagentStop, TeammateIdle, TaskCreated, TaskCompleted |
| Session end | PreCompact, PostCompact, Stop, StopFailure, SessionEnd, Notification |
| Workspace | WorktreeCreate, WorktreeRemove |

### Dispatch Rules

1. **All matching matcher groups fire** — not just the first match
2. Within each group, hooks execute **sequentially in array order**
3. First **blocking decision** (exit code 2) stops the pipeline
4. Hooks from plugins and settings are **additive** — they don't override each other
5. **Async hooks** fire-and-forget; cannot block operations

### Data Passed to Hooks (stdin JSON)

Common fields: `session_id`, `transcript_path`, `cwd`, `permission_mode`, `hook_event_name`
- PreToolUse adds: `tool_name`, `tool_input`
- PostToolUse adds: `tool_response`
- SubagentStop adds: `success`, `agent_transcript_path`

### Environment Variables Available to Hooks

`CLAUDE_SESSION_ID`, `CLAUDE_PROJECT_DIR`, `CLAUDE_ENV_FILE`, `CLAUDE_CODE_REMOTE`, plus custom `env` block variables from settings.json.

### Error/Timeout Handling

| Exit Code | Behavior |
|-----------|----------|
| 0 | Success — parse stdout as JSON |
| 2 | Blocking error — stderr fed to Claude |
| Other | Non-blocking — Claude continues |
| Timeout | Non-blocking — Claude continues |
| Crash | Non-blocking — Claude continues |
| Invalid JSON | Non-blocking — Claude continues |

Default timeout: 10 minutes (configurable per-hook in seconds).

### Hook Output Protocol (PreToolUse)

```json
{
  "hookSpecificOutput": {
    "permissionDecision": "allow|deny|ask",
    "updatedInput": {"field": "modified_value"}
  },
  "systemMessage": "Explanation for Claude"
}
```

---

## Context Window Management

### System Prompt Assembly Order

1. Intro section (agent identity, ~200 tokens)
2. Output style instructions
3. System section (core behavioral rules)
4. Doing tasks section (coding guidelines)
5. Actions section (reversibility/blast radius)
6. **`__SYSTEM_PROMPT_DYNAMIC_BOUNDARY__`** — cache breakpoint
7. Environment section (model, cwd, date, platform)
8. Project context (CLAUDE.md discovery, git status)
9. Instruction files (all CLAUDE.md contents, budget-enforced)
10. Runtime config
11. Appended sections (skills, agents, plugins, hook instructions, MCP instructions)

Everything above the boundary (items 1-5) is **static and cacheable**. Everything below **changes per-session**.

### CLAUDE.md Loading

For each directory from root to cwd:
1. `{dir}/CLAUDE.md`
2. `{dir}/CLAUDE.local.md`
3. `{dir}/.claude/CLAUDE.md`
4. `{dir}/.claude/instructions.md`

**Budget enforcement:**
- **4,000 chars** per individual file
- **12,000 chars** total across all instruction files
- Files exceeding limit are truncated with `[truncated]`

**Loading priority:** Managed policy > Project CLAUDE.md > User CLAUDE.md > Rules

### Context Window Layout (~200K tokens)

| Component | Typical Tokens | Notes |
|-----------|---------------|-------|
| System prompt | 4,200 | Hidden, cached |
| Auto memory (MEMORY.md) | 680 | Hidden |
| Environment info | 280 | Hidden |
| MCP tools (deferred names) | 120 | Hidden |
| Skill descriptions | 450 | Hidden, dropped on compact |
| ~/.claude/CLAUDE.md | 320 | Hidden |
| Project CLAUDE.md | 1,800 | Hidden |
| **Startup overhead total** | **~7,850** | **~3.9% of context** |
| File reads | 1,100-2,400 each | Dominates context |
| Hook outputs | 100-120 each | No truncation! |
| Bash outputs | ~1,200 each | Truncated at 30K chars |

### Compaction

- **Trigger:** Auto when context approaches limit, or manual `/compact [instructions]`
- **Preserves:** Last 4 messages verbatim, CLAUDE.md (re-read from disk), auto memory, system prompt, MCP tools
- **Drops/Summarizes:** All older messages, full tool outputs, intermediate reasoning, skill descriptions
- **Compression ratio:** ~12% of original tokens retained
- **Summary includes:** Tools mentioned, recent requests, pending work, key files (up to 8), timeline

### Prompt Caching

- Up to 4 cache breakpoints per API request (tools, system, messages)
- Minimum thresholds: Opus 4,096 tokens, Sonnet 2,048 tokens
- Tool definition changes **invalidate all three cache levels**
- Adding deferred tools via ToolSearch can break the cache if not handled carefully

---

## MCP Integration

### Tool Naming Convention

```
mcp__{normalized_server_name}__{normalized_tool_name}
```

Normalization: Only `a-z`, `A-Z`, `0-9`, `_`, `-` kept. All other chars replaced with `_`. Special handling for `claude.ai` servers (collapsed underscores).

### Server Transport Types

| Type | Config Key | Fields |
|------|-----------|--------|
| Stdio | `"stdio"` (default) | command, args, env |
| SSE | `"sse"` | url, headers, oauth |
| HTTP | `"http"` | url, headers, oauth |
| WebSocket | `"ws"` | url, headers |
| SDK | `"sdk"` | name |
| ClaudeAI Proxy | `"claudeai-proxy"` | url, id |

### Connection Lifecycle

1. **Discovery:** Config files loaded in order: User → Project → Local (last writer wins per server name)
2. **Lazy spawning:** Servers not started until tools actually needed
3. **Initialization:** JSON-RPC handshake with `protocolVersion: "2025-03-26"`
4. **Tool discovery:** `tools/list` with pagination, tools indexed in `BTreeMap`
5. **Tool invocation:** Lookup qualified name, ensure server ready, send `tools/call` with raw name
6. **No automatic reconnection:** Crashed servers produce IO error on next call

### Deferred Tools System

- **Core tools always present:** bash, read_file, write_file, edit_file, glob_search, grep_search
- **All other tools deferred:** Listed by name only in `<system-reminder>` tags
- **ToolSearch:** Model calls this to fetch full schemas on demand
- **`ENABLE_TOOL_SEARCH=auto`:** Load full schemas upfront if they fit within 10% of context

### Config Merge: Last Writer Wins

If same server name in User and Project config, **Project definition wins**. No merge of server configs — full replacement by name. Plugin MCP servers use prefix `plugin:pluginName:serverName` to avoid collisions.

### CCR Proxy URL Unwrapping

URLs containing `/v2/session_ingress/shttp/mcp/` are proxy URLs. The `mcp_url` query parameter is extracted to get the real endpoint. Server signatures use unwrapped URLs for deduplication.

---

## Plugin System

### Plugin Types

| Type | Location | plugin.json | Enablement | CLAUDE_PLUGIN_ROOT |
|------|----------|------------|------------|-------------------|
| Marketplace | `~/.claude/plugins/cache/marketplace/plugin/version/` | Required | `enabledPlugins` in settings | Cache directory |
| Inline | `.claude/` in project or `~/.claude/` globally | Not required | Automatic | `.claude/` directory |

### Plugin Directory Structure

```
plugin-name/
├── .claude-plugin/
│   └── plugin.json          # Manifest (required for marketplace)
├── CLAUDE.md                # System prompt injection
├── .mcp.json                # MCP server definitions
├── agents/                  # Subagent definitions (.md)
├── skills/                  # Skills (subdirs with SKILL.md)
│   └── skill-name/
│       └── SKILL.md
├── hooks/
│   └── hooks.json           # Event handler configuration
│   └── *.js                 # Hook scripts
└── scripts/                 # Shared utilities
```

### Hook Merging (Additive)

All sources are merged additively:
1. User settings hooks
2. Project settings hooks
3. Local settings hooks
4. Plugin hooks (from each enabled plugin)

**No deduplication.** If two plugins register hooks for the same event+matcher, both run. Execution order between plugins is not deterministic.

### Skill Namespacing

Plugin skills are namespaced: `pluginName:skillName` (e.g., `multiterminal:kanban-task`). Inline skills are NOT namespaced and could conflict.

### Settings Merge Order (last wins for scalars, deep merge for objects)

1. `~/.claude.json` (legacy)
2. `~/.claude/settings.json` (user)
3. `.claude.json` (legacy project)
4. `.claude/settings.json` (project)
5. `.claude/settings.local.json` (local overrides)

---

## Subagent & Teams Architecture

### Three Tiers of Multi-Agent

| Tier | Mechanism | Context | Isolation |
|------|-----------|---------|-----------|
| Subagents (Agent tool) | In-process, same session | Fresh context window | Optional git worktree |
| Agent Teams (TeammateTool) | Separate Claude Code processes | Fully independent contexts | tmux panes, file-based coordination |
| Coordinator Mode | `CLAUDE_CODE_COORDINATOR_MODE=1` | One worker per CPU | Workers get WORKER preamble |

### Built-in Subagent Types

| Type | Purpose | Permission Level |
|------|---------|-----------------|
| general-purpose | Full tool access (default) | Parent's level |
| Explore | Search/research, fresh context | ReadOnly |
| Plan | Planning mode, read-only tools | ReadOnly |
| Verification | Build/test verification | Parent's level |
| claude-code-guide | Help/guidance | ReadOnly |
| statusline-setup | Terminal config | WorkspaceWrite |

### Context Inheritance

**Subagents GET:** System prompt, CLAUDE.md files, personal/project memory
**Subagents DON'T GET:** Parent conversation history, tool results, file read cache

The `prompt` string is the **sole data channel** from parent to subagent. Even trivial subagents consume ~60K tokens due to inherited system prompt and memory.

### Team Communication

- **SendMessage tool:** Peer-to-peer via filesystem mailboxes at `~/.claude/teams/{team-name}/messages/{session-id}/`
- **Task coordination:** File-based at `~/.claude/tasks/{team-name}/`
- **Teammate execution modes:** tmux (default), in-process, auto

### Worktree Isolation

- `isolation: "worktree"` creates a temporary git worktree on a new branch
- Multiple agents can modify same files in parallel without conflicts
- No changes = auto-cleanup; changes = worktree path and branch returned to parent

### SubagentStart/SubagentStop Event Data

**SubagentStop provides:** `agent_id`, `agent_transcript_path`, `tool_use_id`, `stop_hook_active`, `success`
**SubagentStart:** Supports `matcher` to target specific agent types by name

---

## Channels & Messaging

### How Channels Work

A channel is an MCP server with experimental capabilities that can **push events** into Claude's conversation:

```javascript
capabilities: {
  experimental: {
    'claude/channel': {},              // Push notification listener
    'claude/channel/permission': {},   // Permission relay
  }
}
```

Push events appear as `<channel source="..." from="...">content</channel>` XML tags in the conversation.

### The Channel Warning

When a channel is not on the approved allowlist, Claude Code shows a confirmation prompt with "Loading development channels" text.

**Suppression options:**
1. CLI flag: `--dangerously-load-development-channels server:multiterminal-channel`
2. MultiTerminal auto-accept: TerminalControl.cs detects the prompt text and sends keystroke "1"

### Permission Relay Protocol

1. Claude wants to run a tool requiring approval
2. Claude Code generates a **5-letter request ID** (alphabet: a-z minus 'l')
3. Sends `notifications/claude/channel/permission_request` to channel server
4. Channel forwards to remote user (e.g., ClaudeRemote on phone)
5. User replies "yes/no/always {id}"
6. Channel sends `notifications/claude/channel/permission` back
7. Local terminal stays open simultaneously — first answer wins

### Bridge / Remote Control

- 31 TypeScript files in `src/bridge/`
- Uses **outbound HTTPS only** — no inbound ports
- JWT-based auth with periodic refresh (every 3h55m)
- WebSocket transport: `wss://{base_url}/v1/code/upstreamproxy/ws`
- Known issues: disconnects every ~25 minutes, close code 1002 treated as permanent

### UDS Inbox (Feature-Gated, Not Shipped)

Unix Domain Socket-based inter-session messaging. Multiple local sessions communicate directly. Still behind the `UDS_INBOX` feature flag.

---

## Permission & Security Model

### 5-Level Permission Hierarchy

```
ReadOnly < WorkspaceWrite < DangerFullAccess < Prompt < Allow
```

| Tool | Required Permission |
|------|-------------------|
| bash, Agent, REPL, PowerShell | DangerFullAccess |
| write_file, edit_file, NotebookEdit | WorkspaceWrite |
| read_file, glob, grep, WebFetch, WebSearch, Skill, ToolSearch | ReadOnly |
| Unknown tools | DangerFullAccess (fail-closed) |

Config aliases: `"default"/"plan"/"read-only"` → ReadOnly, `"acceptEdits"/"auto"` → WorkspaceWrite, `"dontAsk"` → DangerFullAccess

### Bash Security (23 Checks, 16 Modules)

**Input sanitization:** Zero-width space detection, IFS manipulation, null byte injection, Unicode homoglyphs, shell metacharacter abuse, command substitution nesting limits

**Command classification:** Read vs write classification, destructive command detection, sed write detection, network access detection, privilege escalation detection

**Mode enforcement:** Read-only blocks writes, workspace-write blocks commands outside workspace, path validation, symlink traversal detection

**Environment:** Zsh builtin detection, shell selection enforcement, env var injection prevention, PATH manipulation detection

**Execution:** Sandbox decision, background process restrictions, timeout enforcement, output size limits

### Sandbox (Linux Only)

Uses `unshare --user --map-root-user --mount --ipc --pid --uts --fork`. Three filesystem modes: Off, WorkspaceOnly (default), AllowList. **Not supported on Windows** — falls back to non-sandboxed with HOME/TMPDIR redirection.

### Auto Mode

AI classifier that auto-approves tool calls appearing safe. Opt-in dialog required. Operates at WorkspaceWrite level but selectively auto-approves DangerFullAccess escalations.

### Denied Tool Handling

When a tool call is denied, the **denial reason is sent back to the model** as an error tool result, allowing it to adapt rather than silently failing.

---

## Environment Variables & Feature Flags

*Full catalog: [claude-code-env-vars-and-feature-flags.md](claude-code-env-vars-and-feature-flags.md)*

### Most Useful for MultiTerminal

| Variable | Purpose |
|----------|---------|
| `CLAUDE_AUTOCOMPACT_PCT_OVERRIDE` | Override compaction threshold percentage |
| `CLAUDE_CODE_AUTO_COMPACT_WINDOW` | Set auto-compact window size |
| `CLAUDE_CODE_SUBAGENT_MODEL` | Override model for subagents |
| `CLAUDE_CODE_PLAN_V2_AGENT_COUNT` | Control plan agent parallelism |
| `MCP_TIMEOUT` | MCP server connection timeout |
| `MCP_TOOL_TIMEOUT` | Individual tool call timeout |
| `MAX_MCP_OUTPUT_TOKENS` | Cap MCP tool output size |
| `ENABLE_TOOL_SEARCH` | auto/true/false — control deferred tool loading |
| `CLAUDE_CODE_SESSIONEND_HOOKS_TIMEOUT_MS` | SessionEnd hook timeout |
| `CLAUDE_CODE_PLUGIN_SEED_DIR` | Plugin seed directory override |

### Warning Suppressors

`DISABLE_COST_WARNINGS`, `DISABLE_INSTALLATION_CHECKS`, `DISABLE_AUTOUPDATER`, `CLAUDE_CODE_DISABLE_TERMINAL_TITLE`, `CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS`, `CLAUDE_CODE_DISABLE_CLAUDE_MDS`

### Dangerous: CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC

Kills GrowthBook flag evaluation alongside telemetry. **Silently breaks:** Channels, Remote Control, premium model access (Opus 4.6 1M), and all gated features. Use granular flags instead.

### Feature Flags (32 Compile-Time, 15+ Runtime)

**Compile-time (can't enable externally):** KAIROS (daemon), BUDDY (pet), ULTRAPLAN (30-min planning), ANTI_DISTILLATION, UNDERCOVER_MODE, AUTO_MODE, UDS_INBOX, VOICE_MODE

**Runtime (tengu_* via GrowthBook):** `tengu_penguins_off` (Fast Mode kill-switch), `tengu_amber_flint` (Agent Teams gate), `tengu_cobalt_raccoon` (auto-compact), `tengu_anti_distill_fake_tool_injection`

### Internal Codenames

Tengu (Claude Code), Capybara (Claude 4.6 variant), Fennec (Opus 4.6), Penguin Mode (Fast Mode)

---

## Actionable Recommendations for MultiTerminal

### Critical / Do Now

1. **CLAUDE.md is being truncated.** Budget is 12,000 chars total. Our project CLAUDE.md far exceeds this. Move reference tables (database schema, folder map, architecture diagram) into `.claude/rules/` path-scoped files or skills that load on demand. Keep root CLAUDE.md to ~200 lines of critical conventions.

2. **Hook output enters context without truncation.** Every `additionalContext` field from hooks adds tokens permanently. Audit all hooks — keep output minimal. Use stdout on exit 0 for logging (doesn't enter context).

3. **Remove duplicate multiterminal-channel from .mcp.json.** *(Already done this session.)*

### High Priority

4. **Add compaction survival instructions** to CLAUDE.md: "When compacting, always preserve: active task ID, modified file list, current checklist state, test commands."

5. **Consider `ENABLE_TOOL_SEARCH=auto`** for agents with many MCP tools. Our 80+ tools stay deferred by default (good), but some agents may benefit from preloading critical tool schemas.

6. **Use `MCP_TOOL_TIMEOUT` and `MCP_TIMEOUT`** env vars to tune MCP server timeouts for agents that call slow tools.

7. **Implement worker preamble pattern** — when spawning non-coordinator agents, explicitly instruct them not to spawn sub-agents (prevents recursive spawning).

### Medium Priority

8. **Git worktree support for parallel agents.** Add worktree creation to `TerminalSpawner.cs` so parallel agents don't conflict on shared files.

9. **MCP server health monitoring.** Claude Code has `useMcpConnectivityStatus` for UI notifications. Surface MCP server health in our status bar.

10. **Plugin hook deduplication.** If MT and Clarion Assistant both register safety hooks for `PreToolUse > Bash`, the hook runs twice. Add idempotency checks in hook scripts.

11. **Understand config merge order.** Project settings override user settings for MCP servers (last writer wins). Local settings override both. Use this to your advantage for per-project customization.

### Low Priority / Future

12. **Explore in-process agent mode.** Claude Code's `teammateMode: "in-process"` runs teammates without separate terminals. Could reduce overhead for quick tasks.

13. **Consider UDS Inbox integration** when/if it ships publicly — direct peer-to-peer agent communication without REST API middleman.

14. **Investigate auto-approve patterns.** The `coordinatorHandler` auto-approves for autonomous operation. Could build a more granular version (auto-approve reads, prompt for writes) via PreToolUse hooks.

15. **Watch for open-sourcing.** If Anthropic open-sources Claude Code, many of these workarounds become direct code changes.

---

## Sources

### Primary
- `H:/DevLaptop/Projects/claw-code/` — Clean-room Python/Rust reimplementation
- `H:/DevLaptop/Projects/claw-code/rust/crates/runtime/src/` — Rust runtime (prompt.rs, compact.rs, mcp.rs, permissions.rs, config.rs, etc.)
- `H:/DevLaptop/Projects/claw-code/src/reference_data/` — Archived tool/command/subsystem snapshots

### Official Documentation
- code.claude.com/docs/en/context-window
- code.claude.com/docs/en/channels-reference
- code.claude.com/docs/en/agent-teams
- code.claude.com/docs/en/sub-agents
- platform.claude.com/docs/en/docs/build-with-claude/prompt-caching

### Community Analysis
- Alex Kim: claude-code-source-leak analysis
- Victor Antos: "I Read the Leaked Claude Code Source"
- Sebastian Raschka: "Claude Code's Real Secret Sauce"
- DEV Community: Multiple technical breakdowns
- Reddit: r/ClaudeAI, r/LocalLLaMA, r/ClaudeCode, r/programming (15+ subreddits)
- ccleaks.com: Architecture and hidden features documentation
