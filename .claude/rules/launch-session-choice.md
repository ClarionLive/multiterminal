# Project launch: new session, continue last, or resume a past one

Task **ec2ed636** (2026-10-17). Read this before changing `MainForm.OnStartScreenProjectLaunched`,
`SessionCatalog`, `LaunchSessionChoiceModel`, `ClaudeSessionTracker` or the `--resume` argument in
`LaunchCommandBuilder`.

## What the Owner sees

Launching a Claude project from Home, Browse All, the launch caret, the header's Launch Project or
New Project on an existing folder goes through one hook, `OnStartScreenProjectLaunched`. Before the
claude command is built, MT decides:

| Condition | Result |
|---|---|
| Shift held on the click | New session, no prompt |
| Not a Claude kind (Codex, PowerShell) | New session, no prompt |
| `projects.launch_mode` = `new` | New session |
| `launch_mode` = `continue` | Resume the newest resumable **non-helper** session (newest helper only if every candidate is a helper); new if none; the dialog if that one may be open elsewhere |
| `launch_mode` = `ask` (default, NULL, unknown) | `LaunchSessionDialog` if at least one session is resumable, else new |
| Catalog load failed | A message: start new or cancel. Never silently treated as "no history" |

The dialog offers New session (default), Continue last (agent, age, summary), and the last ~15
sessions, helper and worktree sessions included. There is an editable name, prefilled with the
session's original MT name. "Don't ask again" writes `launch_mode` via `ProjectDatabase.SetLaunchMode`
(New maps to `new`, Continue to `continue`; picking a specific session leaves `ask`). Until the project
settings form has a Launch-mode field, the only way back to `ask` is editing the database.

## Why transcripts, not MT's session tables

`session_lineage` has no rows for spawned helpers and its `session_file_path` goes stale when Claude
Code moves a transcript into a worktree's folder. `SessionCatalog` therefore reads Claude Code's own
folders: `~/.claude/projects/<encoded root>` and every `<encoded root>--claude-worktrees-*`. Encoding
is `[^A-Za-z0-9] -> '-'`. It reads only a bounded head and tail of each transcript and caps the number
of transcripts read. Agent names and summaries come from injected DB lookups, with the transcript as
fallback.

## The rules that keep a resume safe

- **The resume cwd is the transcript's own folder.** Claude finds a session only from the directory
  it was started in. `ResolveCwd` returns only a recorded cwd that encodes to the transcript's folder
  (or the project root for the root's own folder); anything else drops the session rather than
  offering one that fails with "No conversation found".
- **Transcript content is untrusted.** `ValidateResumeCwd` checks the cwd *as a string* before any
  filesystem call: a local drive path, canonicalised, equal to the project root or under
  `<root>\.claude\worktrees\<id>`. A UNC path would otherwise make Windows authenticate to a remote
  host just by opening the chooser. It is re-validated immediately before `BuildCommand`. Junctions
  are not resolved (local-trust model).
- **Never two processes on one transcript.** The live set is every MT pane's
  `CurrentClaudeSessionId`, every pane's `PendingResumeSessionId` (reserved the moment a resume
  launches, before the statusline reports anything) and every headless helper's
  `AgentProcess.SessionId`. A pane or helper with no known id yet marks every session written since
  its launch as "may be open elsewhere", which needs a confirmation. Just before registration, the
  live set is rebuilt and the transcript re-checked; a change aborts back to the start screen.
- **Outside MT it is advisory only.** CA tabs and plain terminals are invisible to the live set; the
  only signal is a transcript written in the last ~2 minutes. There is no cross-process lock.
- **Name and launch path.** The resumed pane goes through `LaunchCommandBuilder` like any launch; only
  `--resume <id>` is added (GUID-validated, Claude only). A chosen name equal to the project-derived
  identity goes through `TryRegisterProjectIdentity` (fail closed); any other name is registered with
  `PreRegisterTerminalWithName` and the pane's launch nonce, which must not rotate before
  `StartTerminal`. A held name comes back suffixed, and the pane shows the actual name.
- **`--resume`, never `--continue`.** `--continue` picks the newest session in the cwd, which may be
  live, and cannot reach worktree sessions.
