# MultiTerminal Codebase Reference

A multi-agent coordination system for Claude Code. WinForms desktop app (C#/.NET) with integrated REST API (port 5050), MCP server, and WebView2-based UI panels.

> Agent behavioral instructions (kanban workflow, MCP tools, messaging, task terminology) are in the MultiTerminal plugin CLAUDE.md. This file is only the codebase reference for working on MT source code.
> Detailed reference tables (database schema, folder map, task guides, events) are in `.claude/rules/` and load on demand.

## ⛔ YOU ARE RUNNING INSIDE MULTITERMINAL — READ FIRST

**You (this Claude Code terminal) are ALWAYS one of MultiTerminal's hosted terminals. MT is the desktop app you live inside.** This changes how build/run/deploy work here:

- **NEVER end/kill the running MultiTerminal process.** No `taskkill`, no `Stop-Process MultiTerminal`, nothing that ends MT. Killing it ends your own host (and every sibling terminal). There is no scenario where you do this.
- **NEVER launch `MultiTerminal.exe` yourself.** Don't start a second instance (from `staged`, `Deploy`, or anywhere). The human owns MT's lifecycle. If MT needs (re)starting, ask the human to do it.
- **Build = safe, and does NOT affect the running app.** `dotnet build MultiTerminal.csproj -c Debug` compiles and the csproj `CopyToStaged` target mirrors output into the **shared staged folder** `H:\DevLaptop\ClarionPowerShell\staged` (stamped via `.build-info.json`). **Staged is NOT where MT runs from**, so building never disturbs the live app. Build freely to check for errors.
- **The live app runs from the Deploy folder** `H:\DevLaptop\ClarionPowerShell\Deploy`. It is populated only by `deploy.ps1`, which copies `staged → Deploy`.
- **You CANNOT deploy.** `deploy.ps1` hard-refuses while `MultiTerminal.exe` is running (locked files) — and it's always running because you're in it. **Only the human** can: exit MT → run `deploy.ps1` → relaunch from `Deploy`. So the path to make your code changes go live is: *you build (→ staged); the human deploys + restarts.*
- **Live-testing your changes** therefore means asking the human to deploy+restart first, then exercising the new behavior. You cannot self-serve a "rebuild + restart MT" loop.

## Architecture

```
MainForm.cs (5.2K LOC) - UI Host, 11 WebView2 panels
  REST API (port 5050) - 24 controllers in API/Controllers/
  MessageBroker.cs (5.5K LOC) - Central hub, routes messages, fires events
  SQLite (TaskDatabase.cs 4.6K LOC) - 21+ tables: tasks, sessions, knowledge, profiles
  CodeGraph (Roslyn) - CodeGraphDatabase + CSharpCodeGraphIndexer, cg_ tables in same SQLite
  MCP Server (Node.js) - 91 tools at %APPDATA%/multiterminal/mcp
```

## Code Graph Auto-Indexing

`Services/CodeGraphWatcher.cs` keeps the Roslyn code graph fresh automatically: it watches every registered C# project root (top-level `*.csproj`) for `.cs` changes and runs a **debounced full reindex**, plus a **startup staleness sweep** (and the same one-time bootstrap when a project is registered mid-session) that heals an already-stale or never-indexed graph. Sweep/bootstrap roots SKIP-if-fresh (no needless reindex of an up-to-date graph); real `.cs` edits defer-and-retry so they're never lost. All reindexes — watcher AND the manual `POST /api/code-graph/index` / `index_code_graph` MCP tool — funnel through `Services/CodeGraphIndexCoordinator.cs` (a single global `SemaphoreSlim(1,1)`) so two non-atomic rebuilds can't corrupt the `cg_` tables on the shared SQLite connection. A **per-project freshness floor** (`project:{name}:last_indexed` metadata) prevents thrash without one project suppressing another. Constructed + `Start()`ed in `MainForm` next to `_teamWatcher`; disposed before the DB.

Tuning env vars (all optional; `MULTITERMINAL_*` convention, read once at startup):

| Var | Default | Range | Effect |
|-----|---------|-------|--------|
| `MULTITERMINAL_CODEGRAPH_WATCH` | on | `0`/`false`/`off`/`no`/`disabled` disable (case-insensitive); `1`/`true`/`on`/`yes`/`enabled` enable | Master switch; when off, no watchers install. Unrecognized values log a warning and are treated as on. |
| `MULTITERMINAL_CODEGRAPH_DEBOUNCE_MS` | `12000` | clamped to `[3000, 600000]` | Quiet window after the last `.cs` change before a reindex fires. |
| `MULTITERMINAL_CODEGRAPH_MIN_INTERVAL_MS` | `300000` | clamped to `[0, 86400000]` (0 disables floor) | Per-project freshness floor — skip/defer reindex if the project was indexed within this window. |

Present-but-unparseable or clamped values are logged (`DebugLogService` "CodeGraphWatcher") so a typo is distinguishable from intent.

## Critical Files

| File | LOC | Role |
|------|-----|------|
| `MCPServer/Services/MessageBroker.cs` | 5,512 | Central hub. Routes messages, caches tasks/terminals/profiles, fires events. |
| `Services/TaskDatabase.cs` | 4,621 | SQLite CRUD for 21+ tables. |
| `MainForm.cs` | 5,162 | UI host. Creates/docks panels, wires events. |
| `MCPServer/Models/KanbanTask.cs` | 476 | Core task model with status, checklist, plan, continuation notes. |
| `Services/CSharpCodeGraphIndexer.cs` | 380 | Roslyn-based 2-pass C# code indexer (symbols + relationships). |
| `Services/CodeGraphDatabase.cs` | 250 | SQLite CRUD for code graph (cg_symbols, cg_relationships, cg_projects). |

## Key Patterns

**Adding a UI Panel:**
1. Create `{Name}Panel/{Name}PanelDocument.cs` inheriting `DockContent` (HideOnClose=true)
2. Create inner control with WebView2 or custom renderer
3. Add `Initialize(MessageBroker broker)` and `ApplyTheme(bool isDark)` methods
4. In MainForm: instantiate, Initialize(), wire events, add toolbar toggle button

**Adding a Backend Feature:**
1. Add model to `MCPServer/Models/`
2. Add persistence to `TaskDatabase.cs` (table + CRUD + migration)
3. Add routing to `MessageBroker.cs` (methods + events)
4. Add MCP tool to Node.js server if agents need access
5. Add REST endpoint to `API/Controllers/` if HTTP needed

**Adding an MCP Tool:**
1. Add tool definition in `%APPDATA%/multiterminal/mcp/index.js`
2. Tools call REST API at localhost:5050 -> MessageBroker -> TaskDatabase
3. Return formatted result with success/error context

## Linting

Two analyzer chains run from this repo. Both produce a green build/lint today; warnings are intentional carry-overs flagged for follow-up cleanup.

### C# (NetAnalyzers + StyleCop)

- **Run:** `dotnet build MultiTerminal.csproj -c Debug --no-restore`
- **Where it's wired:** `Directory.Build.props` (TreatWarningsAsErrors, Nullable=enable default, EnforceCodeStyleInBuild) + `.editorconfig` (severity per rule) + `Microsoft.CodeAnalysis.NetAnalyzers` + `StyleCop.Analyzers` PackageReferences in each csproj.
- **Suppress one site:** `#pragma warning disable CAxxxx` / `#pragma warning restore CAxxxx` around the offending line. Add a one-line comment explaining why (e.g. "WHERE clause is constant; user input flows through SQLiteParameter").
- **Suppress whole class/method:** `[System.Diagnostics.CodeAnalysis.SuppressMessage("Category", "CAxxxx", Justification="...")]`.
- **Baseline a noisy new rule:** add `dotnet_diagnostic.CAxxxx.severity = suggestion` (or `silent`) to the `[*.cs]` block in `.editorconfig`. Use `suggestion` for "informational, not enforced", `silent` for "off entirely".
- **Allow a rule to warn but not fail the build:** add it to `<WarningsNotAsErrors>` in `Directory.Build.props`. Use this when you plan to fix the rule's instances later but don't want to block CI today. The block currently carries `CA1001;CA1063;CA1816;CA2000;CA2100;CA2213;CA3003;CA2216;CA3001;CA5394` for follow-up cleanup, plus `CA1848;SA1633` as long-term carve-outs.
- **"Race a TCS against a timeout" pattern:** prefer `await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs))` without a `CancellationTokenSource`. Do **not** introduce `using var cts` + `Task.Delay(timeoutMs, cts.Token)` + `cts.Cancel()` just to satisfy CA2000 — CA2000 doesn't fire without an IDisposable local, and `Task.Delay` runs on a shared process-wide timer queue so letting it drop on the success path is cheap. Some existing sites hoist the delay to a named local for positive-polarity timeout checks (`var timeoutTask = Task.Delay(...); if (winner == timeoutTask) ...`) — that's the dominant dialect (`MainForm.cs:~1099`, `MainForm.cs:~1289`, `Terminal/WebViewTerminalRenderer.cs:~867`, `MainForm.cs:WaitForRendererReadyAsync`); others inline it (`Services/AgentProcess.cs:~473`). Either form is fine — just no CTS. Option A ratified in task `b840ddee`.

### JavaScript (ESLint v9 flat config)

- **Run:** `npm run lint` (or `npm run lint:fix` to auto-fix). Requires `npm install` once after clone.
- **Where it's wired:** `eslint.config.mjs` (flat config) + `package.json` devDependencies (`eslint`, `@eslint/js`, `globals`).
- **Targets:** `hooks/**/*.js` and `scripts/**/*.js` only. Embedded subprojects like `mcp-session-history/` manage their own tooling.
- **Suppress one line:** `// eslint-disable-next-line rule-name` followed by the line.
- **Suppress one block:** `/* eslint-disable rule-name */ ... /* eslint-enable rule-name */`.
- **Suppress unused vars:** prefix the identifier with `_` (e.g. `function foo(_unusedArg) { ... }` or `catch (_e)`). Bare `catch { }` (no binding) is also valid — ESLint won't flag it.
- **Add/loosen a rule:** edit `eslint.config.mjs`. Keep `no-console: off` for hook scripts (they legitimately log).

### When the build's warning count grows

If a new rule starts firing across many files, the right move is usually: (1) decide if it's load-bearing; (2) if yes, fix the few sites and keep severity at `warning`; (3) if no, downgrade to `suggestion` in `.editorconfig` with a one-line comment explaining why. Don't add `#pragma`s everywhere — that's a smell that the rule shouldn't be at warning.

**Before downgrading a rule, reason explicitly about whether it can mask a real bug.** Pure style rules (e.g. `SA1503` "braces omitted" — `if (x) foo();` behaves identically with or without braces) are safe to silence. Rules that catch semantic defects (dispose, injection, nullable deref, race conditions) are not — downgrade those only after fixing the existing sites, never as a way to make the build green. If you can't articulate in one sentence why silencing a rule can't hide a defect, keep it loud.

## Compaction Instructions

When compacting, always preserve: active task ID and title, list of modified files, current checklist state, any test commands or build commands discussed, and continuation notes for session handoff.
