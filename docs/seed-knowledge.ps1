# Institutional Knowledge Base Seed Script
# Run this after the app is deployed with the KnowledgeDatabase feature active.
# Usage: powershell -File seed-knowledge.ps1

$API = "http://localhost:5050/api/knowledge"

function Post-Knowledge {
    param($title, $content, $category, $tags, $confidence = "confirmed")

    $body = @{
        title      = $title
        content    = $content
        category   = $category
        tags       = $tags
        confidence = $confidence
        sourceType = "manual"
    } | ConvertTo-Json -Depth 3

    try {
        $response = Invoke-RestMethod -Uri $API -Method POST -Body $body -ContentType "application/json"
        Write-Host "  OK: $title" -ForegroundColor Green
    } catch {
        Write-Host "  FAIL: $title — $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "Seeding institutional knowledge base..." -ForegroundColor Cyan

Post-Knowledge `
    -title "Windows Bash: never use `$env:VARNAME or %VARNAME% in Bash tool" `
    -content "When running commands in the Bash tool on Windows, environment variable syntax (`$env:VARNAME or %VARNAME%) gets mangled. Hardcode known paths instead. APPDATA = resolve via `$env:APPDATA at runtime. Always prefer Glob/Grep/Read over PowerShell for file operations. MCP location: %APPDATA%\multiterminal\mcp" `
    -category "gotcha" `
    -tags "windows,bash,env-vars,powershell"

Post-Knowledge `
    -title "Checklist transition notes render as literal text — no markdown formatting" `
    -content "Notes from update_task_checklist appear as inbox notifications. Markdown and \n render as literal text — no formatting works. Keep to 1-2 sentences; put details in phase notes instead." `
    -category "gotcha" `
    -tags "kanban,checklist,notes,formatting"

Post-Knowledge `
    -title "Build & Deploy: agents build, owner deploys — never copy to Deploy folder" `
    -content "Running app is at the Deploy folder (the live binary). Agents can run build_project to compile. Agents CANNOT copy files to Deploy. Deploy workflow (owner only): owner closes app -> runs deploy.ps1 -> relaunches MultiTerminal. After successful build, tell the owner to exit + run deploy.ps1 + relaunch." `
    -category "pattern" `
    -tags "build,deploy,workflow,alice"

Post-Knowledge `
    -title "Collaboration formula: 5-step parallel execution pattern" `
    -content "Proven 9/9 success formula: 1. Explore Together (parallel investigation of existing code/db/services), 2. Divide by Strengths (Diana=backend/services, Charlie=hooks/env setup, Bob=refactor/docs), 3. Parallel Execution (4 parallel phases + Verifier + Code Reviewer + Security Auditor), 4. Clean Integration (all wired by end of phase), 5. Verify Together (all specialist agents validate during execution)." `
    -category "pattern" `
    -tags "collaboration,parallel,agents,workflow"

Post-Knowledge `
    -title "Agent naming convention: plain names for MultiTerminal, Agent prefix for Native Teams" `
    -content "Plain names (Alice, Diana, Bob, Charlie) for MultiTerminal kanban/messaging system — interactive, user participates. 'Agent ' prefix (Agent Alice, Agent Diana) for Native Teams coding sprints — fast, parallel, visible in AgentPanel. Haiku agents unreliable for team messaging — may go idle. Use Sonnet minimum." `
    -category "decision" `
    -tags "agents,naming,native-teams,multiterminal"

Post-Knowledge `
    -title "Max 1 Opus per team — Devils Advocate only, all others Sonnet/Haiku" `
    -content "Model budget rule: Maximum 1 Opus per team. Devils Advocate specialist uses Opus. All other specialists (Test Designer, Verifier, Security Auditor, Debugger, Session Distiller) use Sonnet or Haiku." `
    -category "decision" `
    -tags "agents,models,opus,budget,specialists"

Post-Knowledge `
    -title "PowerShell injection: escape single quotes before env var interpolation" `
    -content "Security pattern: use value.Replace(\"'\", \"''\") before interpolating user-provided values into PowerShell commands or env var strings. Single-quote breakout is a common injection vector in PowerShell command construction." `
    -category "gotcha" `
    -tags "security,powershell,injection,escaping"

Post-Knowledge `
    -title "Path traversal prevention: validate paths before use" `
    -content "Security pattern for path inputs: validate with Path.IsPathRooted() to ensure absolute path, reject UNC paths (starting with \\), verify File.Exists() before using. Never trust user-supplied paths without validation." `
    -category "gotcha" `
    -tags "security,path-traversal,validation,unc"

Post-Knowledge `
    -title "Testing workflow: complete ALL testing before any coding fixes" `
    -content "PM/tester testing workflow: set checklist items to testing, present ONE at a time, PM replies pass or fail (with details if fail). Pass -> done. Fail -> coding with failure reason in notes. Complete ALL testing before ANY coding fixes. Fix all failed items (use subagents for parallel fixes), then re-test." `
    -category "pattern" `
    -tags "testing,workflow,checklist,kanban"

Post-Knowledge `
    -title "Default to SMALL tier — only scale up ceremony when genuinely needed" `
    -content "Project management tier preference: use SMALL tier (minimal ceremony, fast execution) by default. Only escalate to MEDIUM or LARGE when the task genuinely requires the extra planning overhead (>3 checklist items, multiple agents, specialist review)." `
    -category "preference" `
    -tags "workflow,tiers,ceremony,planning"

Post-Knowledge `
    -title "git-stint REMOVED — block-mode caused lost edits in worktrees" `
    -content "git-stint has been removed from the MultiTerminal project. Block-mode caused code edits to get lost in worktrees. Still installed globally (npm install -g git-stint) but no config/hooks/rules are in the repo. Do not attempt to use git-stint for branching workflows." `
    -category "decision" `
    -tags "git,git-stint,worktrees,removed"

Post-Knowledge `
    -title "Specialist agents: DA=Opus, others=Sonnet/Haiku" `
    -content "6 specialist agents in .claude/agents/: Devils Advocate (Opus, after planning LARGE tasks), Test Designer (Sonnet, during checklist creation MED/LARGE), Verifier (Sonnet, after coding before review MED/LARGE), Security Auditor (Sonnet, parallel with Code Review LARGE), Debugger (Sonnet, when testing fails MED/LARGE), Session Distiller (Haiku, session end/task completion)." `
    -category "pattern" `
    -tags "specialists,agents,models,workflow"

Write-Host ""
Write-Host "Seeding complete!" -ForegroundColor Cyan
