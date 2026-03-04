-- Institutional Knowledge Base Seed Data
-- Generated from MEMORY.md on 2026-03-04
-- Run this ONCE after the knowledge tables are created by the first app launch.
-- Safe to re-run: INSERT OR IGNORE will skip already-present entries.

-- Each entry: (id, title, content, category, project_id, source_type, source_id, tags, confidence, created_at, updated_at, is_archived)

INSERT OR IGNORE INTO knowledge_entries
  (id, title, content, category, project_id, source_type, tags, confidence, created_at, updated_at, is_archived)
VALUES
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Windows Bash: never use $env:VARNAME or %VARNAME% in Bash tool',
    'When running commands in the Bash tool on Windows, environment variable syntax ($env:VARNAME or %VARNAME%) gets mangled. Hardcode known paths instead. APPDATA = C:\Users\John Hickey\AppData\Roaming. Always prefer Glob/Grep/Read over PowerShell for file operations. MCP location: C:\Users\John Hickey\AppData\Roaming\multiterminal\mcp',
    'gotcha',
    NULL,
    'manual',
    'windows,bash,env-vars,powershell',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Checklist transition notes render as literal text — no markdown formatting',
    'Notes from update_task_checklist appear as inbox notifications. Markdown and \n render as literal text — no formatting works. Keep to 1-2 sentences; put details in phase notes instead.',
    'gotcha',
    NULL,
    'manual',
    'kanban,checklist,notes,formatting',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Build & Deploy: Alice builds, John deploys — never copy to Deploy folder',
    'Running app is at H:\DevLaptop\ClarionPowerShell\Deploy\MultiTerminal.exe (the live binary we are working on). Alice can run build_project to compile. Alice CANNOT copy files to Deploy. Deploy workflow (John only): John closes app -> runs deploy.ps1 -> relaunches MultiTerminal. After successful build, tell John to exit + run deploy.ps1 + relaunch.',
    'pattern',
    NULL,
    'manual',
    'build,deploy,workflow,alice',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Collaboration formula: 5-step parallel execution pattern',
    'Proven 9/9 success formula: 1. Explore Together (parallel investigation of existing code/db/services), 2. Divide by Strengths (Diana=backend/services, Charlie=hooks/env setup, Bob=refactor/docs), 3. Parallel Execution (4 parallel phases + Verifier + Code Reviewer + Security Auditor), 4. Clean Integration (all wired by end of phase), 5. Verify Together (all specialist agents validate during execution).',
    'pattern',
    NULL,
    'manual',
    'collaboration,parallel,agents,workflow',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Agent naming convention: plain names for MultiTerminal, Agent prefix for Native Teams',
    'Plain names (Alice, Diana, Bob, Charlie) for MultiTerminal kanban/messaging system — interactive, user participates. "Agent " prefix (Agent Alice, Agent Diana) for Native Teams coding sprints — fast, parallel, visible in AgentPanel. Haiku agents unreliable for team messaging — may go idle. Use Sonnet minimum.',
    'decision',
    NULL,
    'manual',
    'agents,naming,native-teams,multiterminal',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Max 1 Opus per team — Devils Advocate only, all others Sonnet/Haiku',
    'Model budget rule: Maximum 1 Opus per team. Devils Advocate specialist uses Opus. All other specialists (Test Designer, Verifier, Security Auditor, Debugger, Session Distiller) use Sonnet or Haiku.',
    'decision',
    NULL,
    'manual',
    'agents,models,opus,budget,specialists',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'PowerShell injection: escape single quotes before env var interpolation',
    'Security pattern: use value.Replace("''", "''''") before interpolating user-provided values into PowerShell commands or env var strings. Single-quote breakout is a common injection vector in PowerShell command construction.',
    'gotcha',
    NULL,
    'manual',
    'security,powershell,injection,escaping',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Path traversal prevention: validate paths before use',
    'Security pattern for path inputs: validate with Path.IsPathRooted() to ensure absolute path, reject UNC paths (starting with \\), verify File.Exists() before using. Never trust user-supplied paths without validation.',
    'gotcha',
    NULL,
    'manual',
    'security,path-traversal,validation,unc',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Testing workflow: complete ALL testing before any coding fixes',
    'John''s testing workflow: set checklist items to testing, present ONE at a time, John replies pass or fail (with details if fail). Pass -> done. Fail -> coding with failure reason in notes. Complete ALL testing before ANY coding fixes. Fix all failed items (use subagents for parallel fixes), then re-test.',
    'pattern',
    NULL,
    'manual',
    'testing,workflow,john,checklist,kanban',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Default to SMALL tier — only scale up ceremony when genuinely needed',
    'Project management tier preference: use SMALL tier (minimal ceremony, fast execution) by default. Only escalate to MEDIUM or LARGE when the task genuinely requires the extra planning overhead (>3 checklist items, multiple agents, specialist review).',
    'preference',
    NULL,
    'manual',
    'workflow,tiers,ceremony,planning',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'git-stint REMOVED — block-mode caused lost edits in worktrees',
    'git-stint has been removed from the MultiTerminal project. Block-mode caused code edits to get lost in worktrees. Still installed globally (npm install -g git-stint) but no config/hooks/rules are in the repo. Do not attempt to use git-stint for branching workflows.',
    'decision',
    NULL,
    'manual',
    'git,git-stint,worktrees,removed',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  ),
  (
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))),
    'Specialist agents: DA=Opus, others=Sonnet/Haiku',
    '6 specialist agents in .claude/agents/: Devils Advocate (Opus, after planning LARGE tasks), Test Designer (Sonnet, during checklist creation MED/LARGE), Verifier (Sonnet, after coding before review MED/LARGE), Security Auditor (Sonnet, parallel with Code Review LARGE), Debugger (Sonnet, when testing fails MED/LARGE), Session Distiller (Haiku, session end/task completion).',
    'pattern',
    NULL,
    'manual',
    'specialists,agents,models,workflow',
    'confirmed',
    datetime('now'),
    datetime('now'),
    0
  );
