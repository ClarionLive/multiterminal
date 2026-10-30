# Changelog

All notable changes to MultiTerminal are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.0.0] — 2026-10-30

Major release: Claude profiles keep work and personal projects apart, and
sessions now survive a relaunch: resume a past session at project launch, bring a
PM's helpers back with it, and restore every open terminal when MT starts again.
The Project pane is replaced by a project settings form, and terminals now talk to
each other without Claude Code's development channel.

### Added

- **Claude profiles.** A profile names a Claude Code config folder
  (`CLAUDE_CONFIG_DIR`), and each project chooses one, so work and personal
  accounts never mix. Every pane, helper and agent in a project's folders runs
  under its profile, and a missing or mismatched profile refuses the launch with a
  message instead of quietly falling back to Default.
- **Settings > Claude Profiles.** Add a profile by name only (its folder,
  `%USERPROFILE%\.claude-<name>`, is created for you), see Signed in / Needs
  sign-in on each row, and press Sign in to log that profile in from a plain
  console. Session history, the token meter and agent teams follow each pane's
  profile. The installer can also put the MT plugin in a shared folder (GH #23).
- **Resume a past session at project launch.** Launching a Claude project asks:
  New session, Continue last (with context used, agent, age and summary), or pick
  one of the last ~15 sessions, helper and worktree sessions included, with an
  editable name. Shift+click always starts new. A per-project Launch mode (ask,
  always new, always continue) is set from "Don't ask again" or project settings.
- **Helpers resume with their PM.** Resuming a PM session reopens the helper panes
  it had open when it last ended (up to 6), each ticked under "Also resume
  helpers". Closing a PM's tab asks once whether to close its helpers too; they
  come back when the PM is resumed.
- **Restore terminals at startup.** After MT exits or crashes with Claude terminals
  open, the next start lists them (helpers nested under their PM, all ticked) and
  resumes the checked sessions into their old panes, showing "Restoring <name>…"
  while it works. With nothing to restore MT opens on a single Home pane.
- **Project settings form.** The gear on a terminal's status bar, a Home card or a
  Browse All row opens one form for every project setting, from General and Launch
  to Git, Agents and Danger zone. A save never overwrites changes made elsewhere and
  says when a setting only applies at the next launch.
- **Notes for the Owner from agents.** Agents can leave notes with
  `add_project_note` / `list_project_notes`, optionally in a category. The Notes
  tab has a collapsible left rail with pinned notes, folding categories, search and
  a right-click menu, plus an unread count on the tab. Agents never edit your notes.
- **Attention pane.** A card for every session: what it is doing, who is waiting
  on you and for how long, its project and context used, with the 5h/7d quota
  left in the header and configurable activity and alarm styles.
- **Attention: "asks you" or "Your turn".** A question the agent asks in chat
  shows as asking you; every other turn end shows a softer "Your turn".
- **A pane's header and tab show when its agent needs you**, and the header shows
  the project name larger.
- **Attention pane By team grouping.** Each PM card has its helpers listed under
  it, a team with anyone blocked goes to the top, and a PM card says "<helper>
  needs you". The grouping survives a restart, text meets 4.5:1 contrast in both
  themes, and Ctrl+wheel zoom is remembered.
- **Compaction continue.** When an agent compacts itself with `compact_my_context`,
  MT types a continue into that pane once the compaction finishes, so the agent
  picks up its work. A compaction you start yourself is left alone.
- **Helpers you can spawn and close.** New `spawn_helper` MCP tool opens a helper
  pane that collects its job with `get_my_spawn_job`, so a job is never lost to a
  helper that was not listening yet. `close_helper` lets a PM close the helpers it
  spawned, and no other pane.
- **Plan tab.** The HUD draws a ticket's checklist as a dependency graph with a
  plain explanation of each step on hover. The Tasks pane gets its own two-tab HUD
  (Tasks, Plan) that follows the selected card.
- **Quiet start.** A per-project setting that skips the session-start menu and the
  "initializing..." kick on launch (GH #34). Just Claude always starts quiet.
- **Terminal input.** Shift+Enter inserts a newline, every paste is a bracketed
  paste, right-click copies or pastes, a clipboard image pastes as a file path, and
  Shift+F10 opens the terminal menu (GH #22, #24).
- **GitHub bot identity for agents.** A one-click GitHub App registration lets
  agents' `git push` and `gh` act as the bot instead of as you, with a short-lived
  token per command and each comment signed with the agent's name.
- **GitHub watch.** A per-project setting (off by default) checks the project's
  github.com repo: new issues and PRs become To Do tickets, are told to the
  project's agent at its next pause, and push you a summary. Each terminal header
  gets a GitHub row with a link to the repo and the open issue and PR counts, and
  the header grows to fit it.
- **Header and Home.** Browse All, New Project, Just Claude, Open PowerShell and
  Launch Project are header pills, and the M menu is an HTML popup. Settings >
  General picks list or cards for Home; Browse All remembers a 3-column sort.
- **Launch Claude offers to create a missing folder** instead of refusing it, on
  a local drive only (GH #44, PR #48 by Mark Sarson).
- **BUILDING.md**: how to build and run MultiTerminal from source on a clean
  machine (PR #49 by Mark Sarson).
- **Smaller additions:** a clear (x) button in every search box, and an app icon
  for MT and its installer (GH #36).
- **The exit confirmation can be turned off**, with "Don't ask again" on the
  question or Settings > General > "Ask before exiting MultiTerminal" (GH #19).
- **Crash reports.** An exception that would end MT unexplained is now written to
  `%APPDATA%\MultiTerminal\logs\crash-<date>.log` (GH #38, PR #60 by Mark Sarson).

### Changed

- **The Project pane is gone**, replaced by the project settings form. The Project
  Manager and Edit Project dialogs are retired too; Browse All takes over their
  project list, pin and version columns, New Project and delete.
- **Messaging no longer uses Claude Code's development channel.** MT writes
  messages straight into a live session, launches each terminal under its own name
  so the board name is the address, and stops auto-answering the channel dialog.
  Received messages start with `[MultiTerminal message from X to Y]`, and
  `send_message` defaults the sender to the session's own name.
- **ClarionAssistant tabs join MT messaging** and can send and receive by name.
- **Launch names.** A project with no team lead launches under the project name
  (TestB, TestB-2...) instead of "Unassigned". Just Claude offers "Unassigned"
  first and by default, gives each launch its own unique non-team name, and has a
  bigger resizable dialog.
- **Tab titles say the role**, for example "Alice - MultiTerminal (PM)".
- **A new terminal always docks**, never as a tiny floating window, so "Max Tabs
  per Grid" is removed. Grid Layout adds Home tabs, not named PowerShells.
- **The header's agent-name strip is removed** (GH #27).
- **New Project on a folder that already has a project asks** (open, rename,
  choose another folder) instead of overwriting it.
- **The MCP config is repaired before every launch** when missing or broken, and
  left untouched when it works. MT warns at startup when its terminals would start
  without the agent tools.
- **Source-control tokens are no longer handed out by the local REST API.**
- **MCP read tools default to your own name.** `get_messages`,
  `get_my_pickable_tasks`, `get_my_active_task` and `get_active_worktree` use the
  calling session's name when none is given, instead of failing with a cryptic
  error; `get_inbox` still requires `userId` (GH #18).
- **No GitHub App set up is no longer reported as an error.** Agents' `gh` prints
  one calm line and uses your own login (GH #68).
- **xterm.js is told it runs on ConPTY**, turning on its Windows-specific handling,
  notably reflow on resize (GH #51, PR #57 by Mark Sarson).
- **Panes MT opens for agents no longer steal focus while you are typing**; focus
  stays where you are working.
- **Browser-tab tools default to the calling terminal** when no terminal id is given.

### Fixed

- **MT terminals ignored your user settings and user plugins** (GH #52, reported
  by bill-atchison). MT passed `--setting-sources` twice, and Claude Code keeps
  only the last one, so only project settings loaded.
- **Ctrl+wheel zoom did nothing in a Claude pane** once Claude turned on mouse
  tracking, and a restored font size was reset (PR #45 by Mark Sarson).
- **Messages reported as delivered that never arrived** (GH #7); empty message
  bodies are now rejected.
- **The Terminals list kept showing sessions that had quit.**
- **Database writes that waited indefinitely** while the code graph reindexed now
  give up after a bounded wait, and a config file with a repeated key no longer
  crashes startup.
- **A crash from the Debug panel** updating its list off the UI thread.
- **HUD zoom reset on every launch** for six of the seven tabs; each tab now keeps
  its own zoom, and the saved HUD split is restored.
- **Panels opened light while the app was dark.**
- **`update_checklist` erased checklist fields** it did not name.
- **The Inbox panel stranded most of the unread backlog**, and the dashboard badge
  could show another user's unread count (GH #6).
- **A closed terminal's late activity brought back an unnamed Attention card.**
- **Switch Project's search box reversed typed text.**
- **Text boxes in Settings and New Project clipped their text.**
- **`get_task_detail` and `get_my_active_task` now include the task description**
  (GH #13).
- **Each terminal started ~77 git.exe processes every 10 seconds** to refresh the
  HUD's changed-files state; the poll now reuses what it already knows.
- **An empty pane behind the New Project dialog was solid green**; the focus border
  is now drawn as an outline.
- **A blank terminal id in a browser-tab tool** now gives a clear "not found" error
  instead of a server error.
- **A spawned helper could start in another repository** when an older agent with
  the same name had an active task there; it now stays in the folder it was spawned into.
- **Resume missed worktree sessions of a project registered below its git repo
  root** (for example ClarionAssistant), and a skipped resume now names its real reason.
- **Past sessions offered at project launch** (and Continue last, helper resume and
  startup restore) no longer include the background agents MultiTerminal runs to
  write checklist explanations; new ones leave no transcript at all (GH #67).
- **MT could crash at startup or shutdown** building UI off the UI thread before the
  window existed, or from timers still running at close (GH #38, PR #60 by Mark Sarson).
- **The header's branch pill overlapped the active task title** (GH #70, PR #71 by
  Mark Sarson).
- **The Tasks HUD did not follow an agent's activation** and its Active tab had no
  count (GH #54, PR #58 by Mark Sarson).
- **Cancelling a header action left an empty Home tab behind** (PR #64 by Mark Sarson).
- **The installer did not ship the Everything SDK DLLs**, so the everything-search
  MCP server exited at startup (GH #42).

### Upgrade notes

- **Node.js 24 is required** (GH #50, reported by Mark Sarson). The plugin's hooks use a database binary
  built for Node 24; on any other major version the activity feed, Attention
  clearing and session state stop working. The installer now checks the version
  and warns.
- **Existing projects run under the Default profile** until you choose one in the
  project settings form. Default never sets `CLAUDE_CONFIG_DIR`, so nothing moves.
- **The first start after upgrading offers no restore**: 2.1.0 recorded no session
  ids, so MT opens on one Home pane. Restore works from the next exit or crash on,
  and only sessions launched under 3.0.0 can bring their helpers back.
- **A saved layout that contained the Project pane** simply drops it.
- **The installer no longer writes the agent-teams setting**; MT sets
  `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` on every launch instead.

### Known issues

- **A launch in the first minute after MT starts can freeze the window for several
  seconds** while the code graph reindexes your projects (always after an
  upgrade). The startup restore shows "Restoring <name>…" meanwhile; the freeze
  itself is being fixed.
- **Bitbucket accounts that use app passwords stop working**, because Atlassian
  retired app passwords (GH #62).
- **This release still targets .NET 8**, which ends support on 10 Nov 2026. A .NET 10
  update follows as 3.0.1 (GH #47).

## [2.1.0] — 2026-07-18

Feature release: phone/remote workflow, a proactive worktree janitor with human
alerting, and project-scoped task/worktree resolution. No breaking changes — a
drop-in upgrade over 2.0.0.

### Added

- **Ask-Owner remote question flow.** New `ask_owner` MCP tool and
  `POST /api/ask-owner` let an agent pose a blocking question that routes to the
  Owner's phone and waits for the answer, so agents no longer stall silently when
  the Owner is away.
- **Phone arrival indicators.** Permissions and Alerts badges surface pending
  items on the phone, notifications support read-all, and the gateway now returns
  detailed 400 diagnostics.
- **Janitor human alerting.** Actionable janitor findings are routed to the
  team-lead inbox, escalated to a severe-tier push, and surfaced at session start —
  so stranded worktrees and blocked merges get a person's attention instead of
  sitting silent in the feed.
- **Project-scoped pickable tasks.** A new pickable endpoint + MCP tool surface the
  next tickets by *project* rather than by agent, with reassign-on-claim.
- **Installer: opt-in user-global MCP registration.** User-global MCP registration
  is now opt-in (default off) during install (GH#2).
- **Task HUD Active / Not-Active tabs** with per-tab UX: readability pass,
  description tooltips, search, and a sort toggle.

### Fixed

- **Janitor blind to the `.claude/worktrees` layout.** `WorktreeLayout` now derives
  the repo root for the modern `{repoRoot}/.claude/worktrees/{id}` layout, so the
  stranded-dir scan and Pass-3 orphan removal no longer report a silent false
  `ok/0/0` while real husks sit on disk. Underivable parents now degrade the scan to
  *partial* instead of vanishing, a child-shape gate (`IsWorktreeIdSegment`) guards
  Pass-3 deletes, and rootPrefix separators are normalized so forward-slash-registered
  projects don't drop their own stranded dirs.
- **Cross-project task/worktree leakage.** Active-task and worktree resolution are now
  project-scoped; an explicit-target guard stops a team lead's active-task worktree
  from hijacking a New Project launch.
- **Push reliability.** Valid default VAPID subject + `BadDeviceToken` pruning; the
  push success ratio no longer misreports (post-prune denominator no longer hides
  failed/pruned sends).
- **Alerts badge unread tracking.** Rewired to a gateway-native watermark with
  hardened read-all (clamped `seenThrough`, monotonic `ReceivedAt`) and a `LoadSeen`
  repair that heals a poisoned watermark across restarts.
- **HUD Notes delete-confirm popup** was clipped invisible by the tab bar — now
  rendered fixed-position on `document.body`.
- **Terminal document leak** on tab close — `TerminalDocument` is now disposed via
  `ContentRemoved`.
- **CI unblock:** removed a useless escape in the `verify-writepath` regex.

### Docs & Chore

- Point README doc quick-links at the GitHub Pages site (GH#3); add `.nojekyll` so
  `docs/` serves statically; add a "Running from source: is the plugin required?"
  troubleshooting FAQ.
- Repo cleanup: removed the dead `PromptTreeDocument` panel (kept its live event
  args), ephemeral test-artifact docs, a dead `TestSpawnForm`, `knowledge-seed.sql`,
  and 16 orphaned dev-scratch PowerShell scripts.

[2.1.0]: https://github.com/ClarionLive/multiterminal/releases/tag/v2.1.0
[2.0.0]: https://github.com/ClarionLive/multiterminal/releases/tag/v2.0.0
