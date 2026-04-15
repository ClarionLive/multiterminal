# Claude Code Hooks System: Internal Architecture Report

**Date:** 2026-03-31
**Purpose:** Internal research to improve MultiTerminal hook integration
**Sources:** claw-code repo analysis, official docs, leaked source analysis, production hook implementations

---

## 1. Complete Hook Event Catalog

Claude Code supports **21+ lifecycle events** as of v2.1.88. These fire at specific points during a session, ordered here by lifecycle phase:

### Session Setup Events

| Event | When It Fires | Matchers | Notes |
|-------|---------------|----------|-------|
| **SessionStart** | Session begins (startup, resume, `/clear`, `/compact`) | `startup`, `resume`, `compact` | Can inject text into the first user prompt |
| **InstructionsLoaded** | After CLAUDE.md and instruction files are loaded | None documented | Rare use |
| **ConfigChange** | When settings change during session | Config key name | |

### User Input Events

| Event | When It Fires | Matchers | Notes |
|-------|---------------|----------|-------|
| **UserPromptSubmit** | User presses Enter to submit a prompt | None (fires for all prompts) | Input contains original prompt text |
| **CwdChanged** | Working directory changes | None | Receives `old_cwd` and `new_cwd` |

### Tool Lifecycle Events (Agentic Loop)

| Event | When It Fires | Matchers | Notes |
|-------|---------------|----------|-------|
| **PreToolUse** | Before a tool executes | Tool name (e.g., `Bash`, `Edit\|Write`, `mcp__*`) | Can block, deny, ask, or modify input |
| **PermissionRequest** | When a tool needs user permission | Tool name | Can approve/deny programmatically |
| **PostToolUse** | After a tool executes successfully | Tool name | Receives `tool_response` |
| **PostToolUseFailure** | After a tool execution fails | Tool name or empty | Receives error details |
| **Elicitation** | When Claude requests structured input from user | None | Supports command/http hooks only |
| **ElicitationResult** | When user responds to an elicitation | None | |
| **FileChanged** | When a file is modified | File glob pattern | Receives `file_path` and `event` |

### Agent Lifecycle Events

| Event | When It Fires | Matchers | Notes |
|-------|---------------|----------|-------|
| **SubagentStart** | A subagent (Task tool) spawns | Empty string or agent type | |
| **SubagentStop** | A subagent finishes | Empty string or agent type | Receives `success` boolean |
| **TeammateIdle** | A teammate agent becomes idle | None | Added ~v2.1.33 |
| **TaskCreated** | A task is created | None | |
| **TaskCompleted** | A task is completed | None | Added ~v2.1.33 |

### Session End Events

| Event | When It Fires | Matchers | Notes |
|-------|---------------|----------|-------|
| **PreCompact** | Before context compaction | None | Save state before memory trim |
| **PostCompact** | After context compaction | None | |
| **Stop** | Claude finishes responding | None | Main agent turn complete |
| **StopFailure** | Claude's response fails | None | |
| **SessionEnd** | Session terminates | None | |
| **Notification** | Various notification types fire | `idle_prompt`, `permissionprompt`, `authsuccess`, `elicitationdialog` | |

### Workspace Events

| Event | When It Fires | Matchers | Notes |
|-------|---------------|----------|-------|
| **WorktreeCreate** | A git worktree is created | None | |
| **WorktreeRemove** | A git worktree is removed | None | |

---

## 2. Hook Configuration Schema

### Settings-Based Hooks (Legacy)

In `~/.claude/settings.json` or `.claude/settings.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "node /path/to/safety-hook.js",
            "timeout": 5
          }
        ]
      }
    ]
  }
}
```

### Plugin Hooks (Current Standard)

In `~/.claude/plugins/marketplaces/<marketplace>/plugins/<plugin>/hooks/hooks.json`:

```json
{
  "description": "Plugin description",
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "node \"${CLAUDE_PLUGIN_ROOT}/hooks/safety-hook.js\"",
            "timeout": 5,
            "async": false
          }
        ]
      }
    ]
  }
}
```

### Schema Per Matcher Group

Each event contains an **array of matcher groups**. Each matcher group has:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `matcher` | `string` | No | Pipe-separated regex pattern (e.g., `"Edit\|Write\|Bash"`). Empty string or omitted = match all. |
| `hooks` | `array` | Yes | Array of hook handler objects |

### Schema Per Hook Handler

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `type` | `string` | Yes | - | `"command"`, `"http"`, `"prompt"`, or `"agent"` |
| `command` | `string` | For `command` type | - | Shell command to execute |
| `timeout` | `number` | No | 10 (minutes) | Timeout in seconds (was 60s before v2.1.3) |
| `async` | `boolean` | No | `false` | If true, fire-and-forget; don't wait for result |

### Variable Substitution in Commands

- `${CLAUDE_PLUGIN_ROOT}` - Resolves to the plugin's root directory
- `${CLAUDE_PROJECT_DIR}` - Resolves to the project root
- `${CLAUDE_SESSION_ID}` - Current session ID (v2.1.9+)

---

## 3. Hook Dispatch and Execution Order

### Discovery and Merge Order

Hook configurations are merged from multiple sources in priority order (lowest to highest):

1. **Plugin defaults** - Each enabled plugin's `hooks/hooks.json`
2. **User settings** - `~/.claude/settings.json` `hooks` key
3. **Project settings** - `.claude/settings.json` `hooks` key
4. **Local settings** - `.claude/settings.local.json` `hooks` key
5. **Managed (policy) settings** - Enterprise/managed configurations

For scalar values, later sources override earlier ones. For arrays (like the matcher group arrays under each event), arrays are **concatenated and deduplicated**. This means plugin hooks and settings hooks both fire -- they don't replace each other.

### Execution Sequence Within an Event

When an event fires (e.g., `PreToolUse` for a `Bash` call):

1. **Collect all matcher groups** across all sources (plugins + settings) for this event type
2. **Evaluate each matcher** against the event context:
   - For `PreToolUse`/`PostToolUse`: matcher tests against `tool_name`
   - For `Notification`: matcher tests against notification type
   - For `SessionStart`: matcher tests against `startup`/`resume`/`compact`
   - Empty matcher or no matcher = always matches
3. **Execute all matching hooks in definition order** (array order within each source)
4. **Within a matcher group**, multiple hooks execute **sequentially** in array order
5. **If any hook blocks** (exit code 2, or JSON `decision: "block"`, or `continue: false`), remaining hooks do NOT execute

### Key Rules

- **All matching groups fire**, not just the first match. If three matcher groups match `Bash`, all three run.
- **Async hooks** (`"async": true`) fire immediately and Claude continues without waiting. Their output (if any) is delivered on the next conversation turn.
- **Sync hooks** block Claude's processing until they complete or time out.
- **First block wins**: If any synchronous hook returns a blocking decision, subsequent hooks in the pipeline are skipped.

### Observed from claw-code (Rust reimplementation)

The `config.rs` in claw-code shows that hooks are stored as part of the merged settings object and are accessed via `config.get("hooks")`. The deep merge logic (`deep_merge_objects`) merges JSON objects recursively -- for the hooks key, since each event maps to an array, and arrays at the same key in different sources get the later source's array (not concatenated at the config level). This means **per-event, the last source wins** for direct settings, but the **plugin system handles concatenation separately** before settings merge.

From the Rust test in `config.rs`:
```rust
// User settings: {"hooks":{"PreToolUse":["base"]}}
// Project settings: {"hooks":{"PostToolUse":["project"]}}
// Result: hooks object contains BOTH PreToolUse AND PostToolUse
// (deep merge preserves both keys since they're different)
```

---

## 4. Data Passed to Hook Commands (stdin)

Hook commands receive JSON on **stdin** (not as arguments, not as environment variables). The JSON structure varies by event type.

### Common Fields (All Events)

```json
{
  "session_id": "abc123-def456",
  "transcript_path": "/home/user/.claude/sessions/abc123.jsonl",
  "cwd": "/current/working/directory",
  "permission_mode": "acceptEdits",
  "hook_event_name": "PreToolUse"
}
```

### PreToolUse / PermissionRequest

```json
{
  "session_id": "...",
  "transcript_path": "...",
  "cwd": "...",
  "permission_mode": "...",
  "hook_event_name": "PreToolUse",
  "tool_name": "Bash",
  "tool_input": {
    "command": "dotnet build MultiTerminal.sln"
  }
}
```

### PostToolUse

```json
{
  "session_id": "...",
  "hook_event_name": "PostToolUse",
  "tool_name": "Bash",
  "tool_input": {
    "command": "dotnet build MultiTerminal.sln"
  },
  "tool_response": {
    "exit_code": 0,
    "stdout": "Build succeeded.",
    "stderr": ""
  }
}
```

### PostToolUseFailure

```json
{
  "session_id": "...",
  "hook_event_name": "PostToolUseFailure",
  "tool_name": "Bash",
  "tool_input": { "command": "..." },
  "error": "Command timed out after 120 seconds"
}
```

### SubagentStart

```json
{
  "session_id": "...",
  "hook_event_name": "SubagentStart",
  "agent_id": "agent-uuid",
  "agent_type": "general-purpose",
  "subagent_type": "general-purpose",
  "description": "Task description",
  "prompt": "Initial prompt text",
  "name": "Agent name"
}
```

### SubagentStop

```json
{
  "session_id": "...",
  "hook_event_name": "SubagentStop",
  "agent_id": "agent-uuid",
  "agent_type": "general-purpose",
  "subagent_type": "general-purpose",
  "success": true,
  "agent_transcript_path": "/path/to/subagent/transcript.jsonl"
}
```

### SessionStart

```json
{
  "session_id": "...",
  "transcript_path": "...",
  "cwd": "...",
  "hook_event_name": "SessionStart"
}
```
Matcher values: `"startup"` (fresh session), `"resume"` (resumed session), `"compact"` (after compaction).

### UserPromptSubmit

```json
{
  "session_id": "...",
  "hook_event_name": "UserPromptSubmit",
  "prompt": "The user's submitted prompt text"
}
```

### CwdChanged

```json
{
  "session_id": "...",
  "hook_event_name": "CwdChanged",
  "old_cwd": "/previous/directory",
  "new_cwd": "/new/directory"
}
```

### FileChanged

```json
{
  "session_id": "...",
  "hook_event_name": "FileChanged",
  "file_path": "/path/to/changed/file.ts",
  "event": "change"
}
```

### Notification

```json
{
  "session_id": "...",
  "hook_event_name": "Notification",
  "notification_type": "idle_prompt"
}
```

---

## 5. Environment Variables Available to Hooks

Hook processes inherit the current shell environment plus Claude Code's own variables:

| Variable | Description |
|----------|-------------|
| `CLAUDE_SESSION_ID` | Current session ID (v2.1.9+, also available in stdin JSON) |
| `CLAUDE_PROJECT_DIR` | Project root directory |
| `CLAUDE_ENV_FILE` | Path to a file where hooks can write `export VAR=value` lines to persist env vars for subsequent Bash tool calls |
| `CLAUDE_CODE_REMOTE` | Set to `"true"` in remote/web environments |
| `CLAUDE_PLUGIN_ROOT` | (In command strings) Resolved to the plugin directory |
| Any custom `env` vars | From settings.json `env` block (e.g., `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`) |
| `MULTITERMINAL_NAME` | (Custom) Set by MultiTerminal's launch process for agent identification |

### CLAUDE_ENV_FILE Mechanism

Hooks can write to this file to set environment variables that persist for subsequent Bash commands in the session:
```bash
echo 'export MY_VAR=value' >> "$CLAUDE_ENV_FILE"
```
Use `>>` (append) to preserve variables set by other hooks.

---

## 6. Hook Output Format and Control Flow

### Exit Code Protocol

| Exit Code | Behavior |
|-----------|----------|
| **0** | Success. Parse stdout as JSON if present. |
| **2** | **Blocking error.** Stderr content is fed back to Claude as an error message. Remaining hooks in the pipeline are skipped. |
| **Other** | Non-blocking error. Stderr shown in verbose mode (`Ctrl+O` or `--debug`). Execution continues. |

### JSON Output (stdout, on exit 0)

When a hook exits 0 and writes JSON to stdout, Claude Code parses it for control fields:

#### Top-Level Control Fields

```json
{
  "continue": true,
  "decision": "block",
  "reason": "Explanation shown to Claude",
  "suppressOutput": false,
  "systemMessage": "Injected as a system message to Claude",
  "additionalContext": "Extra context for Claude"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `continue` | `boolean` | If `false`, Claude stops ALL processing. Ultimate override. Default: `true`. |
| `decision` | `string` | `"approve"` (bypass permission), `"block"` (prevent tool call), or omit |
| `reason` | `string` | Explanation. For `block`: fed to Claude. For `approve`: shown to user only. |
| `suppressOutput` | `boolean` | If `true`, hide hook output from transcript |
| `systemMessage` | `string` | Injected as system-level context to Claude |
| `additionalContext` | `string` | Additional context delivered to Claude |

#### hookSpecificOutput (PreToolUse / PermissionRequest)

For finer-grained tool control, use `hookSpecificOutput`:

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PreToolUse",
    "permissionDecision": "deny",
    "permissionDecisionReason": "Blocked: rm -rf on root is not allowed.",
    "updatedInput": {
      "command": "echo 'modified command'"
    }
  }
}
```

| Field | Values | Description |
|-------|--------|-------------|
| `permissionDecision` | `"allow"`, `"deny"`, `"ask"` | Allow (auto-approve), deny (block + tell Claude why), ask (prompt user) |
| `permissionDecisionReason` | string | Explanation for the decision |
| `updatedInput` | object | **Modify the tool's input** before execution (e.g., change the Bash command) |

**Important:** Use either exit codes OR JSON output per hook, not both. Exit 0 + JSON is the structured approach. Exit 2 is the simple approach.

### Async Hook Output

For hooks with `"async": true`:
- Claude fires the hook and continues immediately without waiting
- If the async process eventually exits 0 with JSON containing `systemMessage` or `additionalContext`, that content is delivered to Claude on the **next conversation turn**
- This is useful for non-blocking side effects (database writes, API calls, notifications)

---

## 7. Matcher Pattern Matching

### How Matchers Work

The `matcher` field is a **pipe-separated list of strings** tested against the event's target:

| Event Type | Matcher Tests Against |
|------------|----------------------|
| `PreToolUse` / `PostToolUse` / `PostToolUseFailure` | `tool_name` (e.g., `"Bash"`, `"Edit"`, `"mcp__multiterminal__create_task"`) |
| `PermissionRequest` | `tool_name` |
| `SessionStart` | Session type: `"startup"`, `"resume"`, `"compact"` |
| `Notification` | Notification type: `"idle_prompt"`, `"permissionprompt"`, `"authsuccess"`, `"elicitationdialog"` |
| `FileChanged` | File path (basename, supports glob patterns) |
| Other events | No matcher filtering (always fire) |

### Matching Rules

1. **Empty string or omitted matcher** = matches everything for that event
2. **Pipe-separated values** = OR logic. `"Edit|Write|Bash"` matches if tool_name is ANY of those.
3. **The match is a string inclusion test**, not a full regex. `"Bash"` matches `"Bash"` exactly. `"mcp__multiterminal"` would match any MCP tool containing that string.
4. **Multiple matcher groups** for the same event all run independently if they match.

### Real-World Example (from MultiTerminal plugin)

```json
"PreToolUse": [
  {
    "matcher": "Edit|Write|Bash|Task",
    "hooks": [{ "type": "command", "command": "node .../activity-hook.js", "async": true }]
  },
  {
    "matcher": "AskUserQuestion",
    "hooks": [{ "type": "command", "command": "node .../ask-user-relay-hook.js", "timeout": 130 }]
  },
  {
    "matcher": "Bash",
    "hooks": [{ "type": "command", "command": "node .../safety-hook.js", "timeout": 5 }]
  }
]
```

When a `Bash` tool call fires PreToolUse:
1. First matcher group matches (`Bash` is in `Edit|Write|Bash|Task`) -- activity-hook.js runs async
2. Second matcher group does NOT match (`Bash` != `AskUserQuestion`)
3. Third matcher group matches (`Bash`) -- safety-hook.js runs synchronously

All matching groups run. Within each group, hooks run in array order.

---

## 8. Async vs Sync Hook Execution

### Synchronous Hooks (default)

- Claude **blocks** until the hook process exits or times out
- Hook output (JSON on stdout) is parsed and applied immediately
- Blocking decisions (`deny`, `block`, exit 2) take effect before the tool executes
- Appropriate for: safety guards, permission decisions, input modification, context injection

### Asynchronous Hooks (`"async": true`)

- Claude fires the hook and **immediately continues** processing
- The hook receives the same JSON input on stdin
- Hook runs in the background; Claude does not wait for it
- If the hook produces JSON output with `systemMessage` or `additionalContext`, it is delivered on the **next turn**
- Cannot block or modify tool execution (by the time output arrives, the tool has already run)
- Appropriate for: activity logging, database writes, notifications, analytics, webhook calls

### Practical Pattern

```json
{
  "matcher": "Edit|Write|Bash",
  "hooks": [
    {
      "type": "command",
      "command": "node safety-hook.js",
      "timeout": 5
    },
    {
      "type": "command",
      "command": "node activity-hook.js",
      "async": true
    }
  ]
}
```

Safety hook runs synchronously (can block), activity hook runs async (fire-and-forget logging).

---

## 9. Plugin Hooks vs Settings Hooks Interaction

### Architecture

Claude Code has two hook sources that are **additive, not exclusive**:

1. **Plugin hooks** (`hooks/hooks.json` inside each enabled plugin)
2. **Settings hooks** (`hooks` key in settings.json files at user/project/local levels)

### Merge Behavior

- Plugin hooks are loaded first when a plugin is enabled
- Settings hooks are loaded per the settings precedence chain (user -> project -> local)
- For the same event type, hooks from all sources are **collected and run**
- They do NOT override each other -- a plugin's `PreToolUse` hooks AND settings' `PreToolUse` hooks all fire

### Plugin Isolation

From the MultiTerminal plugin architecture:
- Each plugin's hooks load independently
- Multiple plugins can register hooks for the same event
- Plugin hooks use `${CLAUDE_PLUGIN_ROOT}` for portable paths
- Global `~/.claude/hooks/hooks.json` (legacy) still fires alongside plugin hooks

### Observed Behavior (from MultiTerminal production)

The system currently has:
- **Plugin hooks** in `~/.claude/plugins/marketplaces/multiterminal-marketplace/plugins/multiterminal/hooks/hooks.json` (20 hooks across 13 events)
- **Legacy global hooks** in `~/.claude/hooks/hooks.json` (9 entries -- SessionStart, SessionEnd, Pre/PostToolUse, PostToolUseFailure, SubagentStart/Stop, TeammateIdle, TaskCompleted)
- Both fire. The legacy hooks were the original implementation; plugin hooks are the current architecture.

### Recommendation

Use plugin hooks exclusively. Remove legacy `~/.claude/hooks/hooks.json` entries to avoid double-firing. The plugin system is the intended architecture going forward.

---

## 10. Timeout and Error Handling

### Timeout Behavior

- Default timeout: **10 minutes** (increased from 60 seconds in v2.1.3)
- Configurable per-hook via the `"timeout"` field (in seconds)
- When a hook times out:
  - The hook process is killed
  - A non-blocking error is generated
  - Claude continues execution (the timeout does NOT block the tool)
  - Stderr from the timed-out process is shown in verbose mode

### Error Handling Rules

| Scenario | Behavior |
|----------|----------|
| Hook exits 0, no output | Tool proceeds normally |
| Hook exits 0, JSON output | Parse output for control decisions |
| Hook exits 2 | **Blocking.** Stderr fed to Claude. Tool call stopped. |
| Hook exits 1 or other | Non-blocking error. Stderr shown in debug mode. Tool proceeds. |
| Hook times out | Non-blocking. Tool proceeds. |
| Hook crashes (SIGKILL, etc.) | Non-blocking. Tool proceeds. |
| Hook writes invalid JSON | Non-blocking. Treated as no output. |
| HTTP hook returns non-2xx | Non-blocking. Execution continues. |
| HTTP hook connection fails | Non-blocking. Execution continues. |

### Best Practice for Hook Authors

```javascript
// Always exit 0 -- use JSON for decisions, not exit codes
async function main() {
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
  }

  try {
    const hookData = JSON.parse(input);
    // ... process ...

    if (shouldBlock) {
      console.log(JSON.stringify({
        decision: "block",
        reason: "Explanation for Claude"
      }));
    }
  } catch (err) {
    // Never throw -- silent failure is safer than breaking Claude
    console.error('[Hook Error]', err.message);
  }

  process.exit(0);  // Always exit 0
}
```

---

## 11. Insights from the Source Code Leak (2026-03-31)

The Claude Code npm package v2.1.88 accidentally shipped a `.map` file containing the full TypeScript source (~500K lines, ~2000 files). Key hook-related findings:

### File Structure (from claw-code archive references)

- `types/hooks.ts` -- TypeScript type definitions for all hook events, input/output shapes
- `schemas/hooks.ts` -- Zod/JSON schema validation for hook configurations
- `hooks/toolPermission/PermissionContext.ts` -- Permission decision routing
- `hooks/toolPermission/handlers/interactiveHandler.ts` -- Interactive permission prompts
- `hooks/toolPermission/handlers/coordinatorHandler.ts` -- Coordinator/swarm permission handling
- `hooks/toolPermission/handlers/swarmWorkerHandler.ts` -- Swarm worker permission handling
- `hooks/toolPermission/permissionLogging.ts` -- Permission decision audit logging

### Architecture Observations

1. **Tool permission is a layered system**: Interactive handler (user prompts), coordinator handler (multi-agent), and swarm worker handler each implement the same permission interface but with different policies.

2. **Hooks are part of the settings merge pipeline**: The `deep_merge_objects` function in the Rust reimplementation confirms hooks merge at the JSON object level -- different keys are preserved, same keys get overwritten by higher-priority sources.

3. **The system prompt explicitly mentions hooks**: From the Rust port's `prompt.rs`:
   ```
   "Users may configure hooks that behave like user feedback when
   they block or redirect a tool call."
   ```
   This means Claude is aware hooks exist and treats hook blocks like user rejections.

4. **Anti-distillation mechanisms** (`ANTI_DISTILLATION_CC` flag): The system can inject fake tool definitions to poison training data. This is separate from hooks but shows the tool system's complexity.

5. **Feature flags control hook availability**: New events like `TeammateIdle` and `TaskCompleted` were likely gated behind feature flags before being made public.

---

## 12. Practical Patterns for MultiTerminal

### Pattern: Safety Guard (PreToolUse, sync)

Block dangerous commands before execution. Output JSON with `hookSpecificOutput.permissionDecision = "deny"`.

**Key file:** `safety-hook.js` -- Pure pattern matching, no I/O, targets < 50ms execution.

### Pattern: Activity Logging (Pre/PostToolUse, async)

Record tool usage to database. Use `"async": true` to avoid slowing Claude.

**Key file:** `activity-hook.js` -- Reads stdin JSON, writes to SQLite, handles SubagentStart/Stop.

### Pattern: Context Injection (SessionStart, sync)

Output text to stdout. The text is injected into the first user message via `injectAs: "user-prompt-prefix"` (legacy) or simply by printing to stdout (plugin hooks).

**Key file:** `pool-context.js` -- Reads kanban tasks from DB, outputs formatted context.

### Pattern: User Input Relay (PreToolUse, sync, long timeout)

Intercept `AskUserQuestion` tool, relay to external UI, wait for response. Requires long timeout (130s).

**Key file:** `ask-user-relay-hook.js` -- HTTP relay to MultiTerminal REST API at port 5050.

### Pattern: Input Modification (PreToolUse, sync)

Modify tool input before execution using `hookSpecificOutput.updatedInput`:

```json
{
  "hookSpecificOutput": {
    "hookEventName": "PreToolUse",
    "permissionDecision": "allow",
    "updatedInput": {
      "command": "safe-wrapper.sh original-command"
    }
  }
}
```

### Pattern: Session State Tracking (SessionStart/SessionEnd, sync)

Update database with session start/end events. Map session IDs to agent names.

**Key file:** `session-status-hook.js` -- Writes to `session_agent_map` table for session-to-agent tracking.

---

## 13. Known Issues and Gotchas

1. **Double-firing**: If hooks are defined in BOTH legacy `~/.claude/hooks/hooks.json` AND a plugin's `hooks/hooks.json`, they fire twice. Solution: Remove legacy hooks after migrating to plugin architecture.

2. **Windows stdin bug** (Issue #17424): PreToolUse hooks on Windows sometimes receive empty stdin. Workaround: Check for empty input and exit 0 gracefully.

3. **Hook not executing** (Issue #6305): PreToolUse and PostToolUse hooks sometimes fail to execute while other hook types work. Appears to be a selective system failure in certain versions.

4. **Timeout units confusion**: The `"timeout"` field in hook config is in **seconds**, despite documentation occasionally implying minutes. The overall system timeout was raised to 10 minutes in v2.1.3.

5. **better-sqlite3 dependency**: Hooks that write to SQLite need `better-sqlite3` which requires native compilation. Hook scripts must handle the case where this module is not found.

6. **Exit code 2 vs JSON**: Choose one approach per hook. Using exit code 2 AND JSON output simultaneously produces undefined behavior.

7. **Async hooks cannot block**: Setting `"async": true` on a safety hook means it cannot actually prevent dangerous operations. Always use sync for safety-critical hooks.

---

## 14. Sources

- [Hooks reference - Claude Code Docs](https://code.claude.com/docs/en/hooks)
- [Automate workflows with hooks - Claude Code Docs](https://code.claude.com/docs/en/hooks-guide)
- [Plugins reference - Claude Code Docs](https://code.claude.com/docs/en/plugins-reference)
- [Claude Code Hook Control Flow - Steve Kinney](https://stevekinney.com/courses/ai-development/claude-code-hook-control-flow)
- [Claude Code Hooks Complete Guide - SmartScope](https://smartscope.blog/en/generative-ai/claude/claude-code-hooks-guide/)
- [Hook Development Skill - anthropics/claude-code](https://github.com/anthropics/claude-code/blob/main/plugins/plugin-dev/skills/hook-development/SKILL.md)
- [Claude Code Source Leak Analysis - Alex Kim](https://alex000kim.com/posts/2026-03-31-claude-code-source-leak/)
- [Claude Code Source Leak - VentureBeat](https://venturebeat.com/technology/claude-codes-source-code-appears-to-have-leaked-heres-what-we-know)
- [Claude Code Source Leak - Hacker News](https://news.ycombinator.com/item?id=47584540)
- [PermissionRequest hook issue #19298](https://github.com/anthropics/claude-code/issues/19298)
- [PreToolUse hooks empty stdin on Windows #17424](https://github.com/anthropics/claude-code/issues/17424)
- claw-code repo: `rust/crates/runtime/src/config.rs`, `rust/crates/runtime/src/prompt.rs`
- MultiTerminal plugin: `hooks/hooks.json` (20 hooks, 13 events)
- MultiTerminal production hooks: `safety-hook.js`, `activity-hook.js`, `session-status-hook.js`, `pool-context.js`
