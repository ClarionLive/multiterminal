# Working on the MultiTerminal repo (MT-only rules)

The universal agent rules (tasks, testing, messaging, events, context) come from the MultiTerminal
plugin's `agent-rules.md`, printed into every session by its `agent-rules-hook.js`. This file holds
only what applies to working on THIS repository. It replaced the MT-only half of the old
`multiterminal-rules.md` (task 9d3404d9).

## Build and deploy

The authority is `.claude/CLAUDE.md`, section "YOU ARE RUNNING INSIDE MULTITERMINAL". In short:

- A build (`dotnet build MultiTerminal.csproj -c Debug`, or `mcp__windows-build-runner__build_project`)
  compiles and mirrors to the **staged** folder. Staged is the build mirror, not where the app is
  meant to run, so building normally never disturbs the running app (if MT is running from staged,
  build with `-p:SharedStagedPath=<a scratch folder>`).
- The live app is meant to run from the **Deploy** folder, populated only by `deploy.ps1`. Do not
  assume it does: the human sometimes runs it from staged. Check the running exe's path and read
  the folder's `.build-info.json` stamp before concluding what is live.
- You cannot deploy (`deploy.ps1` refuses while MT runs) and must never start or stop
  `MultiTerminal.exe`. Deploy is the human's: exit MT, run `deploy.ps1`, relaunch.
- After a successful build that should go live, tell John it is ready and that he needs to deploy.

## Exploring before coding

Before writing something new, look for it in `Services/`, `MCPServer/Models/`, `MCPServer/Services/`
and `Services/TaskDatabase.cs` first (`.claude/rules/folder-map.md` and `task-file-guide.md` map them).

## This machine

`APPDATA` is `C:\Users\John Hickey\AppData\Roaming`. Hardcode it in Bash tool commands rather than
using `$env:APPDATA` or `%APPDATA%`, which the Bash tool mangles.

## License

`THIRD-PARTY-NOTICES.md` at the repo root must ship with any distribution. Update it when you add a
dependency.
