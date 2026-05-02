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

## 11. Phase 2 Rollout — auto-commit at task done

**Task `1211ba68`** — Phase 2 of 4. Closes Phase 1's "prune refuses on dirty worktree" gap by auto-committing any changes in the task's worktree before the existing prune step runs. Same env-var gate (`MULTITERMINAL_WORKTREE_MODE=on`); default OFF.

### Architecture shipped

- **`Services/WorktreeAutoCommitService.cs`** (NEW, ~250 lines) — wraps `git status / add / commit / rev-parse` via `Process` invocation. Stateless; takes `TaskDatabase`. Returns structured `AutoCommitResult` (Success / CommitHash / ChangedFiles / UnlinkedFiles / Stderr / SkippedReason) instead of throwing, so the lifecycle hook can branch cleanly. Commit message templated from task title + `ImplementationSummary` + `Task: {taskId}` trailer + `Co-Authored-By: {agentName}`.
- **`MCPServer/Services/MessageBroker.cs`** — instantiates `WorktreeAutoCommitService` alongside `WorktreeManager` in ctor. Phase 1's prune-only block in `UpdateTaskStatus` replaced with **commit-then-prune**: auto-commit fires first; on Success (committed or skipped-clean) → prune runs; on Failure → prune skipped, record stays `active` for manual recovery. Four `RecordActivity` entries cover all outcomes (`auto_commit` / `auto_commit_skipped` / `auto_commit_failed`).

### Decisions ratified during planning

1. Zero changes in worktree at done time → skip commit, run prune normally.
2. Existing manual commits + zero new uncommitted → skip commit, run prune.
3. **Stage explicit file list parsed from `git status --porcelain`** — NOT `git add -A`. (Reason: safety hook + repo policy. See Finding 11.1.)
4. Single squash commit (no per-checklist-item history reconstruction).
5. Pre-commit hooks honored — no `--no-verify` ever. Hook rejection surfaces as commit failure → activity feed → dev resolves and re-marks done → retry.

### Smoke test results (mechanics)

End-to-end smoke run on **2026-05-02** against branch `task/p2smoke` off `master` HEAD `e211ab4`. Replicated the exact git sequence `WorktreeAutoCommitService.CommitForTaskAsync` runs.

| Step | Outcome | Notes |
|---|---|---|
| `git worktree add ../MultiTerminal-worktrees/p2smoke -b task/p2smoke` | PASS | Worktree at expected path. |
| Empty-changes guard (`git status --porcelain` on clean worktree) | PASS | Empty output → service would return Success+SkippedReason='no changes', lifecycle proceeds to prune. |
| Edit fixture file + create new untracked file | PASS | `git status --porcelain` returns ` M README.md\n?? _phase2_marker.txt`. |
| Branch assertion (`git rev-parse --abbrev-ref HEAD` matches record) | PASS | Returned `task/p2smoke`. |
| `git add -- README.md _phase2_marker.txt` (explicit file list, not `-A`) | PASS | Both staged. (See Finding 11.1.) |
| `git commit -m {templated message}` (no `--no-verify`) | PASS | Commit `be8914e` created. Message matched template: subject / body / `Task: phase2smoketest` trailer / `Co-Authored-By: Alice`. Repo's git identity (`peterparker57 <psc.john@gmail.com>`) used as primary author. |
| `git rev-parse HEAD` to capture commit hash | PASS | Returned `be8914eb2819156905afca66834b9db445cda09a`. |
| Branch verification (`git log --oneline -2`) | PASS | Commit landed on `task/p2smoke` only — `master` unchanged. |
| **Post-commit `git worktree remove` (NO `--force`)** | **PASS — Phase 1's gap closed** | Phase 1 alone would have refused on the dirty worktree; now it succeeds because the auto-commit cleaned it up. |
| Full cleanup (worktree dir + branch + parent dir) | PASS | `git worktree list` shows only main; `git branch --list "task/*"` empty. |

### FINDING 11.1 — `git add -A` blocked by safety policy

The repo's safety hook (`safety-hook.js` in the MultiTerminal plugin) blocks `git add -A` and `git add .` with the message: *"git add ./ -A stages everything including secrets. Stage specific files instead."* This is consistent with `CLAUDE.md`'s Git Safety Protocol: *"prefer adding specific files by name rather than using 'git add -A' or 'git add .', which can accidentally include sensitive files (.env, credentials) or large binaries."*

The initial `WorktreeAutoCommitService.CommitForTaskAsync` implementation used `git add -A`; the safety hook caught it during the smoke run. **Mitigation:** the service now parses `git status --porcelain` output, then stages with `git add -- {f1} {f2} ...` (explicit list, `--` separator to prevent flag interpretation). The same parsing was already needed for the unlinked-files note, so no extra cost. Decision (3) above ratified.

**Severity:** would-have-been-blocker if discovered in production. **Mitigated** in code.

### Negative-path smoke (pre-commit hook rejection)

Tested the failure path by installing a temporary hostile `pre-commit` hook in `.git/hooks/`, attempting the same commit sequence on a fresh `task/p2neg` worktree, then removing the hook and retrying.

| Step | Outcome | Notes |
|---|---|---|
| Install `.git/hooks/pre-commit` that exits non-zero | PASS | Hook printed `PRE-COMMIT REJECTED: ...` to stderr and exited 7. |
| Create fresh worktree + edit + `git add -- _neg_marker.txt` | PASS | File staged. |
| `git commit -m "..."` | **PASS — rejected as expected** | Exit code 1; stderr captured: `PRE-COMMIT REJECTED: smoke fixture rejection (auto-test)`. The service's `commitResult.Stderr` would carry this exact text; lifecycle hook sets `shouldPrune=false`. |
| Worktree state after rejection | PASS | File staged but not committed; worktree still "dirty" → Phase 1 prune would refuse. |
| Remove hook (simulate dev fixing the issue) | PASS | `rm .git/hooks/pre-commit`. |
| Retry commit (simulates dev re-marking task done) | PASS | Commit `47136a7` created cleanly. |
| Post-retry-commit prune (no `--force`) | PASS | Worktree removed; branch deleted. |
| Full cleanup verified | PASS | No worktrees, no `task/*` branches, hook gone. |

**Confirms:** (a) pre-commit hooks DO run on worktree commits (worktrees share the main repo's `.git/hooks/`); (b) the service's stderr capture surfaces the failure cleanly; (c) the lifecycle hook's `shouldPrune=false` branch protects the worktree for retry; (d) after the dev resolves the issue and re-marks the task done, the retry succeeds end-to-end.

### FINDING 11.2 — Pre-commit hook latency not surfaced at start time

The activity feed currently logs auto-commit results AFTER the operation completes. If the repo has long-running pre-commit hooks, the dev sees no "running…" state in the meantime — they just observe a delay. Phase 2.x can add a `auto_commit_started` activity entry. **Severity:** nice-to-have polish.

### Lifecycle integration verification (deferred)

The C#/MT integration of the auto-commit lifecycle hook (the `_autoCommit.CommitForTaskAsync(...).GetAwaiter().GetResult()` call inside `MessageBroker.UpdateTaskStatus`) was NOT exercised live in this session — same constraint as Phase 1 (the gate flag is read once at MT startup; current MT instance has it OFF).

User-runnable verification recipe (post-handoff):

```powershell
# 1. Set the gate in a fresh PowerShell session
$env:MULTITERMINAL_WORKTREE_MODE = 'on'

# 2. Launch MT from THAT session
.\bin\Debug\net8.0-windows\win-x64\MultiTerminal.exe

# 3. Create a fixture task linked to the MultiTerminal project, claim+activate.
#    Watch: a worktree appears at MultiTerminal-worktrees/<8charId>/

# 4. Edit a file inside the worktree (use any tool — terminal, an agent, manually).

# 5. Mark the task done.
#    Watch: (a) a commit lands on branch task/<8charId> with the templated message;
#           (b) the worktree directory is removed;
#           (c) the SQLite row in task_worktrees flips to 'pruned';
#           (d) the Activity panel shows a 'worktree / auto_commit' entry with the hash.

# 6. Inspect:
sqlite3 "$env:APPDATA\multiterminal\multiterminal.db" "SELECT * FROM task_worktrees ORDER BY created_at DESC LIMIT 5;"
git log --all --oneline -10
```

No-regression check (gate OFF):
- Launch MT without setting the env var. Activate a task, mark it done. Confirm `task_worktrees` is empty, no worktree directories appeared, no `auto_commit` activity entries fired.

### Implications for Phase 3

- **Auto-merge into main**: needs to switch to the main checkout (NOT the worktree) and run `git merge task/{taskIdShort}`. The task branch's commit(s) are guaranteed clean by Phase 2; merge conflicts are the only remaining hazard.
- **Branch cleanup at Phase 3**: after a successful merge, delete `task/{taskIdShort}` (`git branch -d`). On merge failure, leave the branch around so the dev can resolve.
- **Empty-commit detection**: Phase 2 returns Success with SkippedReason='no changes' when there's nothing to commit. Phase 3 should treat that as "nothing to merge" and skip the merge step entirely — the worktree was already pruned by Phase 2 so there's no cleanup needed either.

---

## 12. Phase 3 Rollout — auto-merge into trunk

**Task `2b98098e`** — Phase 3 of 4. After Phase 1 prune releases the task branch, Phase 3 merges `task/{taskIdShort}` into the main checkout's current trunk and deletes the merged branch. Conflicts trigger an immediate `merge --abort`; main checkout never left half-merged.

### Architecture shipped

- **`Services/WorktreeMergeService.cs`** (NEW, ~220 lines) — wraps `git merge / merge --abort / branch -d / log --oneline / rev-parse / branch --list / status --porcelain` via `Process`. Stateless; takes `TaskDatabase`. Returns structured `MergeResult` (Success / MergedInto / HadConflicts / Stderr / SkippedReason). Same dialect as Phase 2's WorktreeAutoCommitService.
- **`MCPServer/Services/MessageBroker.cs`** — `_merge` instantiated in ctor alongside `_autoCommit` and `_worktrees`. After Phase 1's prune block (and only if `prunedOK==true`), calls `_merge.MergeForTaskAsync`. Failure does NOT roll back commit or prune — those are durable. Three new `RecordActivity` entries: `auto_merge` / `auto_merge_skipped` / `auto_merge_failed`.

### Decisions ratified during planning

1. Merge strategy: plain `git merge --no-edit` (allows fast-forward + merge commits).
2. Trunk detection: `git rev-parse --abbrev-ref HEAD` on the main checkout — cope with `main` / `master` / custom. No schema change.
3. Main-checkout-dirty: `git status --porcelain` first; refuse merge with explicit "commit or stash" message if dirty.
4. Multi-task race: broker dispatch is single-threaded → naturally serialized.
5. No hardcoded default branch.

### Smoke test results — happy path (Phase A)

End-to-end smoke run on **2026-05-02** with master HEAD at `837b9f0` (Phase 2 commit). Used a temporary trunk branch `p3-trunk-happy` to avoid polluting master.

| Step | Outcome | Notes |
|---|---|---|
| Switch main checkout to `p3-trunk-happy` (temp trunk) | PASS | HEAD preserved at `837b9f0`. |
| Create worktree off temp trunk | PASS | `task/p3happy` branch created. |
| Edit + explicit-list `git add` + commit (Phase 2 sim) | PASS | Commit `426b538` on `task/p3happy`. |
| Prune worktree | PASS | Releases branch lock — required pre-merge. |
| Detect trunk: `git rev-parse --abbrev-ref HEAD` | PASS | Returned `p3-trunk-happy`. |
| Branch-existence check: `git branch --list task/p3happy` | PASS | Branch present. |
| Has-commits check: `git log --oneline trunk..task/p3happy` | PASS | One commit ahead. |
| **Dirty-guard refusal** (orphan working-tree changes were present) | **PASS — guard correctly identified dirty state** | Demonstrated negative case in the same smoke. Service would have returned Failure with the "commit or stash" message. |
| Stash orphan changes via `git stash --include-untracked` | PASS | Main checkout now clean. |
| `git merge --no-edit task/p3happy` | PASS | Fast-forward succeeded; commit landed on `p3-trunk-happy`. |
| `git branch -d task/p3happy` (lowercase — refuses unmerged) | PASS | Branch deleted. |
| Cleanup: switch to master, delete temp trunk, pop stash | PASS | Master state at `837b9f0` unchanged; orphan working-tree state restored. |

### Smoke test results — conflict path (Phase B)

| Step | Outcome | Notes |
|---|---|---|
| Stash orphan + create temp trunk `p3-trunk-conflict` | PASS | |
| Create base file `_p3_conflict_base.txt` + commit on temp trunk | PASS | Commit `af67143` — common ancestor. |
| Create worktree off temp trunk → `task/p3conflict` | PASS | Branch + worktree at `af67143`. |
| Edit base file → "TASK VERSION" + commit on task branch | PASS | Commit `8c8b57c` on `task/p3conflict`. |
| Prune worktree | PASS | |
| In main checkout, edit base file → "TRUNK VERSION" + commit | PASS | Commit `b056a16` on `p3-trunk-conflict`. Branches now divergent on same line. |
| `git merge --no-edit task/p3conflict` | **PASS — conflict triggered** | Output: `Auto-merging _p3_conflict_base.txt` / `CONFLICT (content): Merge conflict in _p3_conflict_base.txt` / `Automatic merge failed`. Service captures this via `proc.ExitCode != 0`. |
| Status during conflict: `UU _p3_conflict_base.txt` | PASS | Both versions in conflict markers. |
| `git merge --abort` | PASS | Returned cleanly. |
| HEAD restored (`b056a16` before vs `b056a16` after abort) | PASS — bit-for-bit unchanged | No leftover merge commit, no leftover index entries. |
| Status after abort: clean (empty porcelain) | PASS | |
| **`task/p3conflict` branch PRESERVED** | PASS — branch still listed | Available for dev's manual resolution per the design. |
| Cleanup: switch to master, force-delete temp branches, pop stash | PASS | Master at `837b9f0`; orphan state restored. |

### Note on smoke-tool exit-code reporting

The bash smoke output showed `MERGE_EXIT=0` due to a piped `tail -8` consuming exit-code semantics in the subshell — but git ITSELF exited non-zero on the conflict (visible from the "Automatic merge failed" output). The C# service uses `proc.ExitCode` directly without piping, so it captures the real exit code (1 on conflict). Conflict-detection in production is correct — only the smoke shell's reporting was misleading.

### Findings

No new code-level findings during the Phase 3 smoke. The dirty-guard refusal (Step A8) was anticipated by the design and worked exactly as planned — an unintended bonus negative-case verification.

**FINDING 12.1 (notable, not blocking):** The smoke had to stash orphan working-tree changes before exercising the merge. This mirrors what real users will hit: if the dev has uncommitted work in the main checkout when they mark a task done, Phase 3 will refuse with the "commit or stash" message. The activity feed will surface this clearly. **Severity:** documented behavior; UX polish (toast nudge, "Stash and retry?" button) is a Phase 3.x option.

### Lifecycle integration verification (deferred)

Same constraint as Phase 1 + 2 — the `WORKTREE_MODE` env var is read once at MT startup; current MT instance has it OFF. User-runnable verification recipe (post-handoff):

```powershell
# 1. Set the gate in a fresh PowerShell session
$env:MULTITERMINAL_WORKTREE_MODE = 'on'

# 2. Launch MT from THAT session
.\bin\Debug\net8.0-windows\win-x64\MultiTerminal.exe

# 3. Create a fixture task linked to the MultiTerminal project, claim+activate.
#    (Optional: ensure main checkout is clean first to avoid the dirty-guard refusal.)

# 4. Edit a file inside the worktree.

# 5. Mark the task done.
#    Watch (assuming clean main checkout): (a) auto-commit lands on task/<id>;
#    (b) worktree pruned; (c) merge into the main checkout's current branch
#    (probably 'master' for this repo); (d) task branch deleted; (e) Activity panel
#    shows 'worktree / auto_merge' entry.

# 6. Inspect:
git log --all --oneline -10
sqlite3 "$env:APPDATA\multiterminal\multiterminal.db" "SELECT * FROM task_worktrees ORDER BY created_at DESC LIMIT 5;"
```

Conflict variant: deliberately edit the same file in the main checkout between activating the task and marking it done. Watch the `auto_merge_failed` activity entry appear with the captured stderr; verify the task branch is preserved.

### Implications for Phase 4

- **Panel aggregation**: with Phase 3 in place, the `task_worktrees` table now reliably reflects "which tasks are still in flight" (status='active') vs "which have completed and merged" (status='pruned' AND task is done). Phase 4's HUD panel can use this directly to group changes by task.
- **Contamination retirement**: the `GitAttributionService.GetCrossTaskActiveTaskIds` heuristic was Phase 1's stand-in for "two tasks editing the same file." With Phase 3 shipped, a task's edits live in its own worktree until merge, then land atomically on trunk. Two active tasks CANNOT share working-tree state. The contamination banner becomes unreachable in the gated path; Phase 4 can simplify the panel by removing it.
- **Janitor**: pruned-but-unmerged records (Phase 3 failure mode where commit + prune succeeded but merge failed) will accumulate. Phase 4 needs a sweep that detects these and either re-runs the merge or surfaces them in a "pending merges" UI.

---

## End of audit + Phase 1, 2, 3 rollout

Phase 1 + Phase 2 + Phase 3 functional. With `MULTITERMINAL_WORKTREE_MODE=on`, a kanban task now: spawns a worktree on activate → accumulates work → auto-commits → prunes the worktree → merges into trunk → deletes the task branch. The user's vision — "productivity without git worry" — is materially delivered for the happy path. Conflict path surfaces cleanly via activity feed; dev resolves manually. Phase 4 (panel aggregation + contamination retirement + janitor) is the final polish layer.
