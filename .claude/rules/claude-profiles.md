# Claude profiles: which CLAUDE_CONFIG_DIR a terminal runs under

Ticket **c8ee0eec** (GitHub #21). Read this before changing a launch path, the launch prologue,
anything that reads or writes under a Claude config folder, the project `claude_profile` column, or
the Settings > Claude Profiles tab. The user-facing description is `docs/html/claude-profiles.html`.

**Owner goal:** work and personal projects stay apart. A project must never run under the wrong
profile without the human being told.

A profile is a name for a Claude Code config folder (`Models/ClaudeProfile`). The list is a setting
of the machine (`SettingsService`, key `ClaudeProfiles`); a project stores a profile NAME
(`projects.claude_profile`, also carried in `.claude/project.json`).

## The pieces

| Piece | Where | What it owns |
|---|---|---|
| Folder and environment | `Services/ClaudeProfileResolver` | `Resolve` (name to profile, or a block), `GetPrologue` (pane), `GetEnvironment` (headless), `GetAllRoots`, `Validate`, `Canonicalize`, `DescribeNeverUsed`. Pure cores take a `ClaudeProfileContext`. |
| The launch decision | `Services/ClaudeProfileLaunchPolicy.Decide` | Which profile a launch runs under and whether it may start. Its class summary is the numbered rule list; keep that list and the code in step. |
| The project registry | `Services/ClaudeProfileProjectLookup` | The one builder of the live `ClaudeProfileLookup`. `Configure`d once in MainForm's constructor. |
| Launch sites | `MainForm.DecideLaunchClaudeProfile`, `MainForm.TryGetLaunchClaudeProfile` | Every pane, helper, headless agent and the gloss writer asks one of these two. `TryGet...` is the human-at-the-desk form (one message box per refusal). |
| Pane environment | `ConPtyTerminal.BuildClaudeEnvSetup` | Appends the prologue to every pane's `envSetup`. |
| Headless environment | `AgentProcess.ApplyEnvironment` | Applies the same decision to `ProcessStartInfo.Environment`, after the caller's variables. |
| Session data | `Services/ClaudeProfileDataRoots` | Which config folder is read or written: `ForProfileName`, `ForFolder` / `ForProject`, `AllExisting`, `IsUnderAnyRoot`. |
| Agent teams | `Services/TeamWatcherService` | Watches every root; `BuildTeamKey`. |
| UI | `Dialogs/SettingsWpfDialog` + `Dialogs/ClaudeProfileEditorRules`, `Dialogs/EditProjectDialog` + `Services/ClaudeProfileChoices`, `Dialogs/IdentityPickerDialog`, `Dialogs/IdentityPickerProfileDialog` | Editing the list, choosing per project, picking per launch. |

## Invariants a change must not break

1. **Default never sets `CLAUDE_CONFIG_DIR` to `%USERPROFILE%\.claude`.** When the variable is set,
   Claude Code reads `.claude.json` from INSIDE that folder instead of `%USERPROFILE%\.claude.json`,
   so "explicitly selecting the default" silently moves the user's login, onboarding and global MCP
   state. Default repeats MT's own inherited value when MT was started with one
   (`ClaudeProfileResolver.InheritedConfigDir`), and otherwise REMOVES the variable, so a value set
   by a PowerShell profile script cannot leak in. The rule lives in one private method,
   `ClaudeProfileResolver.ConfigDirValue`; `Validate` also refuses a stored profile that points at
   that folder. Pinned by
   `ClaudeProfileResolverTests.Default_with_nothing_inherited_removes_the_variable_and_never_sets_it`
   and `A_named_profile_pointing_at_home_dot_claude_still_never_sets_it`.

2. **The database row is the authority; `project.json` is a portable copy that can be stale.**
   A launch naming a project takes its row. `ProjectDatabase.SaveRichProject` writes
   `claude_profile` on INSERT only; an existing row changes only through
   `ProjectDatabase.SetClaudeProfile` / `UpdateProjectField`, so a re-save from an old file cannot
   move a project back. A `project.json` is read only at the launch folder and its main-checkout
   twin (`ClaudeProfileLaunchPolicy.LaunchLevels`), never above it, and never against a row that
   owns the folder.

3. **One launch decision.** No launch site reads `Project.ClaudeProfile` off an object it happens to
   hold. All of them go through `ClaudeProfileLaunchPolicy.Decide`, and all of them, together with
   the session-data readers, get the project registry from `ClaudeProfileProjectLookup.Current()`.
   A second wiring of that lookup is how a launch and a transcript read come to disagree about who
   owns a folder. Decide BEFORE registering a name or touching a running session, so a refusal
   leaves things as they were.

4. **Refuse instead of fall back.** A name this machine has no profile for, a profile whose folder
   is missing or invalid, two owners that disagree, a moved or copied folder whose file contradicts
   its row, and a lookup that throws are all refusals. None of them becomes Default.
   `ClaudeProfileResolution.Profile` and `ClaudeProfileLaunchDecision.Profile` are null exactly when
   the launch is blocked, so a caller that ignores `CanLaunch` has nothing to start with. The
   backstop is `ConPtyTerminal.StartProcess`, which turns a prologue that cannot be built into
   `ClaudeProfileRefusedException`; `TerminalStartFailure.Describe` shows that message and does not
   raise `Ready`. The same goes for data: `ClaudeProfileDataRoots` returns null (readers skip,
   writers refuse) and never creates a folder.

5. **A launch nobody is watching is refused under a profile that has never run Claude**
   (`ClaudeProfileResolver.DescribeNeverUsed`, passed to `Decide` only when `unattended`). A pane a
   human opened is not: that is how a profile gets set up. "Cannot tell" counts as used
   (`HasBeenUsed`), because the answer is used to refuse.

6. **Every pane MT starts gets the prologue, whatever it runs.** `BuildClaudeEnvSetup` is not given
   the auto-run command. It used to be, and applied the prologue only when the first token was
   `claude` or the pane was a plain shell; a pane started through a full path to `claude.exe` or a
   wrapper script then ran under whatever the shell already had. Do not give it the command back.
   Pinned by
   `ClaudeProfileLaunchTests.The_environment_tail_cannot_see_the_command_so_it_cannot_skip_a_pane`.
   Consequence: a Codex pane carries the variables too (Codex ignores them), and a project with a
   broken profile refuses its Codex launch like any other.

7. **Roots come only from settings.** `ClaudeProfileResolver.GetAllRoots` is built from the stored
   profiles and MT's startup environment, never from a path a caller supplies. It drops a drive
   root, `%USERPROFILE%` and anything above it, and returns each root with a trailing separator so
   `.claude-other\` is not a prefix of `.claude-otherX`. Every path guard uses it:
   `TokenMeterService.IsAllowedTranscriptPath`, `ClaudeProfileDataRoots.IsUnderAnyRoot` (the session
   lineage and session memory controllers).

8. **A reader that has the pane uses that pane's profile; one that has a folder uses the launch
   decision; only one with no context reads every root.** That is `ForProfileName`
   (`TerminalDocument.LaunchClaudeProfileName`, for example through
   `SessionLineageService.GetClaudeProjectFolderForProfile`), `ForFolder` / `ForProject`, and
   `AllExisting`, in that order of preference. Do not build a `.claude` path from
   `SpecialFolder.UserProfile` in a reader or writer.

9. **Team keys are (root, name).** Two profiles can each have a team with the same name.
   `TeamWatcherService.BuildTeamKey` gives the bare name under the Default root and name plus root
   under any other; subscribers key on the event's `TeamKey`, never `TeamName`.
   `GetConfigRootForProfileName` returns null, not Default's root, for a profile that cannot be
   resolved. Pinned by
   `TeamWatcherServiceRootsTests.Team_key_is_the_bare_name_under_Default_and_differs_for_the_same_name_under_another_root`.

10. **Comparisons are one rule.** Profile names: `ClaudeProfile.NormalizeName`, then
    ordinal-ignore-case (blank and `Default` are the same). Folders:
    `ClaudeProfileResolver.Canonicalize`, then ordinal-ignore-case. Use those two, not a local trim.

## Owner decisions

**2026-10-09**

- Profiles are chosen per project and set up by hand. First-run prompts are not automated; only
  the never-used guard exists (invariant 5).
- The profile NAME is stored in `project.json`.
- MT sets `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1` in every launch
  (`ClaudeProfileResolver.AgentTeamsVariable`), so agent teams do not depend on each profile's
  settings.json.
- Messaging is to be isolated per profile, each profile with its own pool; the Owner and the phone
  may message any terminal; the board stays shared; a project never switches profile. **Not built
  yet** (see gaps).
- Shared plugin marketplace: registered in other profiles as add-only, not enabled.
- No per-profile filter in the UI for now.

**2026-10-10** (these supersede "fall back to Default with a warning" from the day before)

- Refuse, never fall back. One message every time; helpers and headless agents fail with the same
  reason.
- A pane follows its folder: a pane in a registered project's folder takes that project's profile
  however it was opened. Extended to subfolders and worktrees of any name: the nearest registered
  project at or above the folder wins.
- A helper spawned into a folder no project owns takes its spawner's profile.
- The profile is not persisted for restore: a restored pane shows the start screen, so nothing
  could read a stored value.

## Known gaps

- **Messaging and the terminal list do not know about profiles.** `MessageBroker`
  has no profile on a terminal row; an agent in one profile can message one in another.
- **One pool.** `PoolCoordinator` defaults to `%USERPROFILE%\.claude\pool` for every profile, and
  does not follow MT's inherited `CLAUDE_CONFIG_DIR` either.
- **Session-memory search spans profiles.** `SessionMemoryDatabase` is one index with no profile
  column.
- **Oracle always runs under Default.** `OracleTerminalForm.StartTerminal` passes no profile, and
  it is not checked by the never-used guard.
- **Sessions MT did not launch** (ClarionAssistant tabs, see `ca-embedded-sessions.md`) carry no
  profile.
- **The MT plugin path is the Default profile's.** `LaunchCommandBuilder.MtPluginPathCandidates`
  looks at the installer's shared location, then `%USERPROFILE%\.claude\plugins\marketplaces\...`.
  It is passed with `--plugin-dir`, so terminals under any profile get it, but it is found under
  Default's folder unless the installer's shared location was chosen.
- **Codex reads only the Default profile's `.claude.json`** for MCP servers
  (`CodexConfigService`, via `ClaudeProfileDataRoots.DefaultClaudeJsonPath`), on purpose:
  `~/.codex/config.toml` is one file shared by every Codex terminal.
- **Path guards do not follow links.** `Canonicalize` does not resolve symlinks or junctions
  (ticket 47484cc4).
- **A profile removed while panes run under it** leaves those panes running; their session data is
  then skipped (`ForProfileName` returns null) rather than read from Default.
- **Not executed live.** The suite covers the pure decisions. That `StartProcess` puts the prologue
  on the command line, and that each MainForm site passes the decided profile on, is covered by no
  test.
