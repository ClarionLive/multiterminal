# Changelog

All notable changes to MultiTerminal are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
