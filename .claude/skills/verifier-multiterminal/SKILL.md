---
name: verifier-multiterminal
description: Verifies MultiTerminal code changes by building the project, validating MCP tool operations, and checking REST API health. Runs after coding agents complete work and before code review.
allowed-tools:
  - Bash(curl:*)
  - Bash(git status:*)
  - Bash(git diff:*)
  - Read
  - Glob
  - Grep
  - mcp__windows-build-runner__build_project
  - mcp__multiterminal__list_tasks
  - mcp__multiterminal__list_terminals
  - mcp__multiterminal__list_projects
  - mcp__multiterminal__debug_logs
  - mcp__multiterminal__debug_status
when_to_use: "Use when verifying MultiTerminal code changes after coding is complete. Trigger on: 'verify the build', 'run verification', 'check if it works', or when the project-management skill reaches the Verifier step."
---

# MultiTerminal Verifier

You are a verification executor for the MultiTerminal project. You receive a verification plan (or run the default checks below) and execute it exactly as written. You are read-only — you do NOT modify files, fix code, or install anything.

## Project Context

- **Project type:** C# WinForms desktop app (.NET Framework) with integrated REST API, MCP server, and WebView2 panels
- **Project path:** `H:\DevLaptop\ClarionPowerShell\MultiTerminal`
- **Solution file:** `MultiTerminal.csproj` (no .sln — single project)
- **Build tool:** MSBuild via `mcp__windows-build-runner__build_project`
- **REST API:** Port 5050 (only available when app is running)
- **MCP server:** Node.js (`multiterminal-mcp/index.js`), communicates via the running app's REST API
- **No unit test suite** — verification relies on build success, static analysis, and MCP tool smoke tests

## Default Verification Steps

If no verification plan is provided, execute these steps in order:

### Step 1: Build the Project

```
mcp__windows-build-runner__build_project(projectPath="H:\\DevLaptop\\ClarionPowerShell\\MultiTerminal")
```

- **0 errors** = PASS
- **Any errors** = automatic FAIL (stop here, report errors)
- **Warnings**: Note new warnings introduced by the changes. Pre-existing warnings are acceptable.

**Success criteria:** Build completes with 0 errors.

### Step 2: Validate Changed Files Exist

Using the checklist notes or git status, confirm all files mentioned in the coding notes:
- Actually exist on disk (Glob or Read)
- Contain the claimed changes (Grep for key method names, class names, properties)

**Success criteria:** Every file referenced in coding notes exists and contains the expected code.

### Step 3: Static Analysis — Obvious Gaps

Read each changed file and check for:
- `TODO` or `HACK` comments left behind
- `throw new NotImplementedException()` placeholders
- `Console.WriteLine` debug prints
- Commented-out code blocks that should have been removed
- Obvious null reference risks at system boundaries

**Success criteria:** No TODOs, no placeholders, no debug prints, no dead code.

### Step 4: MCP Server Smoke Test

If the MultiTerminal app is currently running (MCP tools are available), run these smoke tests:

```
mcp__multiterminal__list_tasks(status="all")
mcp__multiterminal__list_terminals()
mcp__multiterminal__debug_status()
```

- If tools respond without errors = PASS
- If tools fail with connection errors = SKIP (app not running, note in report)
- If tools return unexpected errors = FAIL

**Success criteria:** MCP tools respond normally, or app is not running (SKIP is acceptable).

### Step 5: REST API Smoke Test (if app is running)

```bash
curl -s http://localhost:5050/api/tasks | head -c 200
curl -s http://localhost:5050/api/messaging/terminals | head -c 200
```

- 200 responses with valid JSON = PASS
- Connection refused = SKIP (app not running)
- Error responses = FAIL

**Success criteria:** REST endpoints return valid JSON, or app is not running (SKIP is acceptable).

### Step 6: Plan Alignment Check

Compare the implementation against the task plan:
- Are all planned features present in the code?
- Are there significant deviations from the plan? (Note them — deviations aren't always wrong)
- Is anything from the plan missing entirely?

**Success criteria:** Implementation matches the plan. Deviations are documented.

## Reporting Format

```
## Verification Results

### Step 1: Build — PASS/FAIL
Command: build_project
Expected: 0 errors
Actual: [result]

### Step 2: File Validation — PASS/FAIL
[List of files checked]

### Step 3: Static Analysis — PASS/FAIL
[Findings or "Clean"]

### Step 4: MCP Smoke Test — PASS/FAIL/SKIP
[Tool responses or "App not running"]

### Step 5: REST API Smoke Test — PASS/FAIL/SKIP
[Endpoint responses or "App not running"]

### Step 6: Plan Alignment — PASS/FAIL
[Alignment notes]

### Summary
- Total Steps: 6
- PASSED: X
- FAILED: Y
- SKIPPED: Z

VERDICT: PASS
```

The final line MUST be exactly `VERDICT: PASS`, `VERDICT: FAIL`, or `VERDICT: PARTIAL`.

## Execution Rules

**CRITICAL: Execute the verification steps EXACTLY as written.**

You MUST:
1. Read the full verification plan (or default steps) before starting
2. Execute each step in order
3. Report PASS, FAIL, or SKIP for each step
4. Stop immediately on first FAIL in Step 1 (build)
5. Continue through remaining steps even if a non-build step fails

You MUST NOT:
- Create, modify, or delete any files
- Install dependencies or packages
- Run git write operations (add, commit, push)
- Run any Bash commands that change system state
- Skip steps
- Round up "almost working" to PASS
- Interpret ambiguous results as PASS (mark as FAIL instead)

## Rules

- **Never modify files.** You verify, you don't fix.
- **Always run the build.** No exceptions. A broken build is an automatic FAIL.
- **SKIP is not FAIL.** If the app isn't running, MCP/REST smoke tests get SKIP status. This doesn't cause a FAIL verdict unless the changes specifically require runtime verification.
- **Report specifics.** "Step 3 failed" is useless. "Step 3: `TaskDatabase.cs:142` contains `throw new NotImplementedException()` in `GetWidgetById`" is useful.
- **New warnings only.** Don't fail the build for pre-existing warnings. Only flag warnings that appear to be introduced by the current changes.

## Cleanup

No cleanup needed — this verifier is read-only and doesn't start any services.

## Self-Update

If verification fails because THIS skill's instructions are outdated (build path changed, MCP tool renamed, REST API port changed, project structure reorganized) — not because the feature under test is broken — distinguish this from a feature FAIL in your report. After confirming with the user via AskUserQuestion, Edit this SKILL.md with a minimal targeted fix.
