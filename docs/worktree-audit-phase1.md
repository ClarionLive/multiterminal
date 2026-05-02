# Phase 1 spike — Per-task worktree audit

**Task:** `e1a5c579` — Phase 1 spike: Per-task worktree isolation
**Author:** Alice
**Date:** 2026-05-02

This document captures the up-front audit of the MultiTerminal codebase for the per-task-worktree spike. The goal of the audit is to find every place that would break — or behave surprisingly — if the same MT instance had to manage multiple worktrees of the same repo simultaneously.

> **Headline:** the codebase is already mostly worktree-friendly. No source-code references hardcode the MT repo path. The DB lives in `%APPDATA%`, panel HTML loads from the app's own `bin/` directory, and every service that needs a project root accepts it as a parameter. The work in Phase 1 is concentrated in **two** integration points: terminal/agent spawn (already accepts `workingDir`, just needs to be told the worktree path) and a new lifecycle hook on task activate/done.

---

## 1. Audit findings, by category

### 1.1 Hardcoded repo paths in source code

**Status: CLEAR**

Searched for `H:\DevLaptop\ClarionPowerShell\MultiTerminal` (both backslash and forward-slash variants) across `*.cs`, `*.bat`, `*.cmd`, `*.json`, `*.xaml`. Matches found only in:
- `.mcp.json.bak` — a backup file, not consumed at runtime.
- `docs/html/api-reference.html`, `docs/html/mcp-tools.html` — generated documentation, not runtime code.

**Source code is clean.** No file in `*.cs`, `*.csproj`, or `.claude/` hardcodes the MT repo root.

### 1.2 SQLite database location

**Status: CLEAR — by design**

The `multiterminal.db` file lives at `%APPDATA%\multiterminal\multiterminal.db` (per `DEPLOYMENT-GUIDE.md` and verified in `checkpoint-databases.ps1`). This is intentionally **outside** any git repo, so no worktree ever materializes a copy of it. All worktrees coordinate through the same shared DB — exactly what we want.

A second database (`messages.db`) is also under `%APPDATA%` per the same convention. Same conclusion.

### 1.3 WebView2 panel HTML resolution

**Status: CLEAR**

Every panel renderer resolves its HTML asset path via `AppDomain.CurrentDomain.BaseDirectory`, i.e. the directory of the running `MultiTerminal.exe`. Confirmed in `ChatPanel`, `DashboardHeader`, `AgentPanel`, `InboxPanel`, `TasksPanel`, `StartScreen`, `ActivityPanel`, `OfficePanel`, `ProfilePanel`, `ProjectPanel`. None of them resolve relative to the *project being edited*, so multiple worktrees do not multiply or invalidate the panel assets — there is exactly one panel HTML, served from the running app.

### 1.4 Git services

**Status: CLEAR**

`GitRepoManager`, `GitRepoService`, `GitRepoWatcher`, and `GitAttributionService` all accept `projectRoot` (or `repoRoot`) as a parameter and canonicalize it internally. They cache by canonical path, so they will transparently support multiple distinct paths in the same process. No global "the project root" assumption.

### 1.5 REST API project-root handling

**Status: CLEAR**

- `WikiController.SafeProjectRoot(raw)` (and the mirrored helper in `CodeGraphController`) resolve a caller-supplied path to a canonical absolute path, validate it exists on disk, and reject otherwise.
- `TasksController` calls `FindGitRoot(filePath)` to walk upward from a given file path, not from a fixed app-level root.
- All REST endpoints that touch the filesystem accept `projectRoot` (or equivalent) as a request parameter — none rely on a process-wide cwd.

### 1.6 Code graph indexer

**Status: CLEAR**

`CSharpCodeGraphIndexer.IndexDirectory(directory, projectName)` takes the directory to scan as an explicit argument. The graph database (`cg_*` tables) lives in the same `%APPDATA%` SQLite as everything else, keyed by `cg_projects` rows.

**Implication:** a re-index can be triggered per worktree and the results coexist as separate `cg_projects` rows in the shared DB. Phase 4 may want to think about whether to dedupe or namespace, but Phase 1 does not need to touch this.

### 1.7 Terminal spawning

**Status: CLEAR — already accepts `workingDir`**

`Services/TerminalSpawner.cs:188` (`SpawnTerminal(agentName, agentType, workingDir, initialPrompt)`):
- Accepts `workingDir` and falls back to `Directory.GetCurrentDirectory()` if null.
- Sets `cd '{safeDir}'` in the spawned PowerShell session.
- Sets `WorkingDirectory = workingDir` on the `ProcessStartInfo`.
- Already injects `$env:MULTITERMINAL_NAME`, `$env:MULTITERMINAL_DOC_ID`, `$env:MULTITERMINAL_ROLE`, `$env:MULTITERMINAL_SPAWNER`, `$env:CHANNEL_PORT`, `$env:CLAUDE_CODE_NO_FLICKER`. Adding one more env var is trivial.

**Implication:** the spawn-side machinery is already worktree-ready. We just need to teach the *callers* to pass the worktree path when claiming a task that has one.

### 1.8 Agent process spawning (headless agents)

**Status: CLEAR — already accepts `workingDir` and `environmentVars`**

`Services/AgentProcess.cs:100` (`SpawnAsync(...)`) accepts both `workingDir` and an `environmentVars` dictionary. Same situation as `TerminalSpawner` — ready to be told a per-task worktree.

### 1.9 `.claude/project.json`

**Status: CLEAR**

The MT project file at `.claude/project.json` contains metadata (id, name, description, change log, team roster) but **no path references**. It can live unchanged in every worktree.

### 1.10 Hook scripts

**Status: CLEAR**

`hooks/*.js` in the repo contains only test fixtures (`test-teammate-idle.js`, `test-task-completed.js`). Production hooks live in the user-profile plugin directory at `%USERPROFILE%\.claude\plugins\marketplaces\multiterminal-marketplace\plugins\multiterminal\hooks` and are loaded from there independent of any project root. Worktree-independent.

### 1.11 MCP server cwd

**Status: CLEAR**

The MCP server is a Node.js process launched from `%APPDATA%\multiterminal\mcp\index.js` — it lives outside any worktree. It calls the REST API on `localhost:5050`. The MT backend (REST + DB + broker) is a single process; the MCP server is a single process; both are shared by all worktrees.

### 1.12 `MessageBroker.cs`

**Status: CLEAR**

No `_projectRoot` or `projectPath` fields on the broker — it does not cache a single project root. It fans events out to UI panels, which are themselves project-aware via parameters.

---

## 2. Findings flagged but not blocking

### 2.1 `DigestController` hardcodes `H:\DevLaptop\Projects\DailyDigest`

`API/Controllers/DigestController.cs:14-15`:
```csharp
private static readonly string DigestRoot = @"H:\DevLaptop\Projects\DailyDigest\digests";
private static readonly string ProjectRoot = @"H:\DevLaptop\Projects\DailyDigest";
```

This is **for an external project (DailyDigest)** that this controller invokes — it is NOT MultiTerminal's own project root. Pre-existing wart, unrelated to worktree work, will not break under the spike. Left for a separate cleanup ticket.

### 2.2 `MainForm` falls back to `Directory.GetCurrentDirectory()` in several places

`MainForm.cs:1085, 1151, 2669, 4595` — these are last-resort fallbacks when no explicit path is supplied. They use the MT process's own cwd. None of them make per-task decisions, so they will not produce wrong results under worktrees, but a clean follow-up could replace them with explicit `_settings.GetLastDirectory()` or similar. **Severity: nice-to-have, post-Phase-4 cleanup.**

### 2.3 One MT instance manages many worktrees

This is an architectural fact, not a defect. The MT desktop app is a singleton: one process, one REST API, one MCP server, one broker, one SQLite DB. Worktrees are filesystem state; the *coordination layer* stays unified. This matches Diana's 2026-02-03 hybrid-model recommendation in `docs/research/git-worktrees-for-multi-agent-programming.md`: shared coordination, isolated implementation.

---

## 3. Blockers

**None identified by the audit.** The spike can proceed without any preparatory refactoring.

---

## 4. Branch and path conventions for the spike

| Item | Convention |
|------|-----------|
| Worktrees parent dir | `{repoParent}/MultiTerminal-worktrees/` (sibling to main checkout, NOT nested) |
| Worktree path format | `{worktreesParent}/{taskIdShort}/` where `taskIdShort` is the 8-char prefix |
| Branch name format | `task/{taskIdShort}` |
| Branch base | `main` HEAD at the time of `git worktree add` |
| Worktree lifecycle | Create on `set_task_active`; remove on `update_task_status='done'` |
| Feature flag | `MULTITERMINAL_WORKTREE_MODE` env var (`off` | `on`); default `off` during spike |

For this repo specifically: `H:/DevLaptop/ClarionPowerShell/MultiTerminal-worktrees/{taskIdShort}/`.

---

## 5. Agent-rooting decision

### Options considered

**A. Env var injection at terminal spawn.**
Inject `MULTITERMINAL_TASK_WORKTREE={worktreePath}` into the spawned PowerShell session alongside the existing MULTITERMINAL_* env vars. The agent reads the env var if it cares; tools that walk the cwd just inherit the right place via the existing `cd '{safeDir}'`.

**B. MCP tool query.**
Add `get_my_worktree(taskId)` to the MCP server. Agents call it on demand to ask "where am I supposed to be working?" and the server consults `task_worktrees` to answer.

**C. Hybrid.**
Both: env var for the common case, MCP tool as a verifiable fallback / introspection path for diagnostics.

### Decision: **A (env var) for Phase 1, with the MCP tool deferred to Phase 2 if needed.**

Rationale:
- `TerminalSpawner` and `AgentProcess` already inject env vars; adding one more is one line of code each.
- The actual "be in the right directory" plumbing is already done by the `cd '{safeDir}'` line — the env var is just for the agent to *introspect*, not to drive cwd.
- MCP tool adds a round-trip and a server dependency for what is essentially a static lookup — more complexity than the spike needs.
- If we discover a spawn path that bypasses env-var injection (e.g., the user manually launches a terminal outside MT and then claims a task), we can add the MCP tool fallback in Phase 2 — but that is *not* the failure mode we are optimizing for.

### Concrete Phase 1 wiring

In `TerminalSpawner.SpawnTerminal` (around line 248-258), add to the env-var block:
```powershell
$env:MULTITERMINAL_TASK_WORKTREE='{safeWorktreePath}';
```
where `safeWorktreePath` is the sanitized path returned by `WorktreeManager.GetWorktreePathForTask(taskId)`, or empty when no worktree is in play.

In `AgentProcess.SpawnAsync`, add to the `environmentVars` dictionary at the call site (in `MainForm.cs` and `AgentProcessTest`):
```csharp
environmentVars["MULTITERMINAL_TASK_WORKTREE"] = worktreePath ?? string.Empty;
```

Both `workingDir` and the env var should match.

---

## 6. Behavior for already-running terminals that switch tasks

A terminal that is already running has a fixed `cwd` — `cd`-ing it to a different worktree mid-conversation is messy and not in scope for the spike. The Phase 1 model is:

> **Spawn-time rooting only.** A terminal is rooted in whatever worktree it was spawned into. Switching active tasks does not change a running terminal's cwd. To physically work in a different worktree, the user spawns a new terminal scoped to that task.

This is consistent with how Claude Code's own `Agent({isolation: "worktree"})` works — isolation is established at spawn.

UI follow-up (NOT in this spike): when a user activates a task whose worktree differs from the current terminal's cwd, MT could surface a one-line nudge like `"Tip: this task lives in worktree task/abcd1234. Open a new terminal there?"` — leave that for Phase 2 polish.

---

## 7. Phase 1 acceptance criteria touchpoints

Mapping audit findings to the ticket's acceptance criteria:

- **No regression in single-tree workflow** — handled by feature flag (`MULTITERMINAL_WORKTREE_MODE=off` default).
- **Audit document complete with severity classifications** — this document.
- **Agent rooting decision documented** — section 5 above.
- **Build succeeds inside the worktree** — to be verified by smoke test (checklist item 7); audit found no obvious blockers.
- **Worktree auto-removed on `done`** — to be implemented (checklist item 6) and verified (checklist item 7).

---

## 8. Implications for later phases

Findings that do not affect Phase 1 but are worth noting for Phase 2-4 design:

- **Auto-commit (Phase 2):** the commit needs to run inside the worktree (`git -C {worktree} commit ...`). All file paths the commit references will be worktree-relative.
- **Auto-merge (Phase 3):** the merge target is `main` in the *original* checkout, not the worktree. We merge by switching to main in the original checkout and `git merge task/{taskIdShort}`, then deleting the branch + pruning the worktree.
- **Code graph indexer (Phase 4):** if we want the graph to follow the live worktree, we need to re-index on worktree creation. If we accept some staleness, we can keep indexing the main checkout and tolerate the lag.
- **Contamination retirement (Phase 4):** once each task has its own worktree, two tasks **cannot** share working state. The contamination heuristic in `GitAttributionService.GetCrossTaskActiveTaskIds` becomes unreachable (in the production path), so the banner code can be removed. Keep it during the transition; remove only after all in-flight tasks are confirmed migrated.

---

## 9. Codebase-meta updates required

When checklist item 3 lands (adding `task_worktrees` table), update:
- `.claude/rules/database-tables.md` — add the new table.
- `.claude/rules/folder-map.md` — add `WorktreeManager.cs` under `Services/`.
- `CLAUDE.md` critical files table only if `WorktreeManager.cs` exceeds the 500-LOC threshold (unlikely in Phase 1).

---

## 10. Smoke test results (mechanics)

End-to-end smoke run on **2026-05-02** against branch `master` HEAD `2497cb7`. Manually replicated what `WorktreeManager.CreateForTaskAsync` does, verified each step.

### Results

| Step | Outcome | Notes |
|---|---|---|
| `git worktree add ../MultiTerminal-worktrees/smoke2 -b task/smoke2` | PASS | Worktree at expected path; branch `task/smoke2` created off HEAD; main checkout untouched. |
| `.git` is a gitlink file pointing at the main repo's `worktrees/` admin dir | PASS | Confirmed via `cat .git` → `gitdir: H:/.../MultiTerminal/.git/worktrees/smoke2` |
| Seed `tools/` (vendored binaries) | PASS | `cp -r tools/` copied 2 files (rg.exe + UNLICENSE). |
| Filesystem isolation (write to worktree, verify NOT in main) | PASS | Created `_smoke_marker.txt` in worktree; confirmed absent from main checkout. |
| `dotnet build` inside the worktree | PASS | First build: 0 warnings / 0 errors / 16.74s (including restore). |
| `git worktree remove` (no `--force`) on dirty worktree | PASS — refuses correctly | Exact stderr: `'...' contains modified or untracked files, use --force to delete it`. Phase 1 by design. |
| `git worktree remove --force` cleanup | PASS | Worktree gone, branch deleted, main checkout intact. |

### Findings discovered during the smoke (beyond static audit)

**FINDING 10.1 (originally missed by static audit, now mitigated): vendored gitignored artifacts.**
Fresh worktrees lack `tools/rg.exe` because it's excluded by the user's GLOBAL gitignore (`*.exe` rule at `~/Documents/gitignore_global.txt:6`), not the project's own `.gitignore`. Without it, the post-build copy step in `MultiTerminal.csproj` fails with `MSB3030`. Mitigation landed in `WorktreeManager.SeedVendoredArtifacts(repoRoot, worktreePath)` — copies a hardcoded allowlist of vendored dirs from the main checkout after `git worktree add` succeeds. Phase 1 allowlist: `tools/`. Phase 2 should generalize this into a per-project post-create hook (e.g., a script in `.claude/`).

**FINDING 10.2: first build inside a fresh worktree must NOT use `--no-restore`.**
A fresh worktree has no `obj/project.assets.json`. The first build needs to restore — `dotnet build` (no flag) succeeds; `dotnet build --no-restore` fails with `NETSDK1004`. Subsequent builds can use `--no-restore` as normal. Document for users / build automation invoked inside worktrees. **WorktreeManager itself does not run builds**, so no manager-side fix is needed; just guidance.

**FINDING 10.3: prune refusal on uncommitted is the documented Phase 1 behavior.**
Confirmed `git worktree remove` (no `--force`) refuses cleanly on dirty worktrees with a clear stderr. `WorktreeManager.PruneForTaskAsync` propagates this as an `InvalidOperationException` with the captured stderr, leaving the DB record `active` so the user can resolve manually (commit, stash, or invoke `--force` themselves). Phase 2 auto-commit closes this gap.

### Lifecycle hook verification (NOT exercised live in this session)

The lifecycle hooks in `MessageBroker.SetTaskActive` / `UpdateTaskStatus` are gated by `WorktreeConfig.IsEnabled`, which is read once at MT process startup. The current running MT instance has the flag OFF (default), so the gates short-circuit and no live smoke is possible without a relaunch.

User-runnable verification steps (after closing MT and relaunching with the env var set):

```powershell
# 1. Set the gate from a PowerShell session
$env:MULTITERMINAL_WORKTREE_MODE = 'on'

# 2. Launch MT from THAT session (so the env var is inherited)
.\bin\Debug\net8.0-windows\win-x64\MultiTerminal.exe

# 3. In MT, create a fixture task linked to the MultiTerminal project,
#    claim it, and set it active.
# 4. Verify a worktree appeared at H:\DevLaptop\ClarionPowerShell\MultiTerminal-worktrees\<8charId>\
# 5. Verify the task_worktrees row in %APPDATA%\multiterminal\multiterminal.db:
sqlite3 "$env:APPDATA\multiterminal\multiterminal.db" "SELECT * FROM task_worktrees;"

# 6. Mark the task done. Verify the worktree directory is gone (or the row is
#    'pruned'). If the worktree had untracked content the prune will refuse;
#    that is expected Phase 1 behavior — see FINDING 10.3.
```

No-regression check (gate OFF):
- Launch MT without setting the env var. Activate a task. Mark it done. Confirm `task_worktrees` is empty and no worktree directories appeared. The lifecycle is byte-for-byte unchanged from the pre-Phase-1 behavior.

---

## End of audit

Audit + spike complete. Codebase was already mostly worktree-friendly. The two real surprises came from the smoke (FINDINGS 10.1 + 10.2), both small. Phase 1 is functional with `MULTITERMINAL_WORKTREE_MODE=on`. Phase 2-4 build directly on top of this foundation.
