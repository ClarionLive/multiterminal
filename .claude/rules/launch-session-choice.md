# Project launch: new session, continue last, or resume a past one

Task **ec2ed636** (2026-10-17). Read this before changing `MainForm.OnStartScreenProjectLaunched`,
`SessionCatalog`, `LaunchSessionChoiceModel`, `ClaudeSessionTracker` or the `--resume` argument in
`LaunchCommandBuilder`. Task **7d20e94c** added helpers resuming with their PM (last section); read
that before changing a pane close path, `MainForm.OnFormClosing`, `AddNewTerminal`'s resume/pin
parameters or `pane_launches`. Task **3e1c09b8** added the restore offer at MT startup (final section);
read that before changing `RestoreSession`, `TryChooseLaunchSession` or `ResumeHelpersFor`.

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

The dialog offers New session (default), Continue last (context used, agent, age, summary), and the last ~15
sessions, helper and worktree sessions included. There is an editable name, prefilled with the
session's original MT name. "Don't ask again" writes `launch_mode` via `ProjectDatabase.SetLaunchMode`
(New maps to `new`, Continue to `continue`; picking a specific session leaves `ask`). The way back to
`ask` is the **Launch mode** field in the project settings form's Launch section (the gear on a terminal's
status bar, a Home card or a Browse All row): Ask each time / Always start a new session / Always continue
the last session. It saves through `ProjectSettingsService`, which writes the column with
`SetLaunchMode` inside the save's transaction, so `SetLaunchMode` stays its only writer; it applies at the
next launch.

### The past-session list (item 7)

An owner-drawn details `ListView` (the dialog is resizable): **Agent** (`[Helper]` marker, `⚠` when the
session may be open elsewhere), **Where** (project folder / worktree `<id>`), **Age**, **Context**,
**Summary** (fills the remaining width). Font is the dialog's +2pt, rows ~1.6x the text height (a
1px-wide `SmallImageList` sets the row height). Double-click or Enter on a row resumes it; while the
list has focus Enter is "Resume selected", not the dialog default. Header, rows and selection are
drawn from the `TerminalTheme`, so a new column is an owner-draw change, not just a `Columns.Add`. The header strip right of the last column is not part of owner drawing (the native header paints it white); `HeaderRemainderPainter` subclasses both lists' headers and repaints it in the header colours after each native paint (task 7d20e94c item 8).

**Context** is the context in use at the session's last main-thread assistant turn, read in
`SessionCatalog` from the transcript tail (`TryReadContextUsage`): `message.usage.input_tokens +
cache_read_input_tokens + cache_creation_input_tokens` (output tokens excluded). Sidechain turns,
`message.model == "<synthetic>"` and all-zero usage are skipped. If the 64KB tail holds no such turn,
one 512KB tail read is tried. The window comes from `message.model` and the line's `requestedModel`
(`ContextWindowFor`): a `[1m]` suffix, Fable/Mythos, any generation-5 model and Opus/Sonnet 4.6+ are
1M; older ids are 200k; anything unrecognised is unknown (tokens only). A window the tokens exceed is
dropped rather than shown as >100%. Text and colour are `LaunchSessionChoiceModel.FormatContext` /
`ContextLevelOf`: `142k / 200k (71%)`, warning at 80%, critical at 90%, `—` with no usage. The same
text leads the Continue-last preview.

**Helper summaries.** A helper's first prompt is only "collect your job", so for a helper the
summary is the first line of its `get_my_spawn_job` result body (after the `YOUR JOB from` header),
matched by `tool_use_id`. In real helper transcripts that result is ~650KB in, past the 256KB head,
so helpers alone get a bounded second read (to 1.5MB / 400 lines) that continues from the end of the
head bytes already in memory (the head is never read twice; a job inside the head costs no read).
Every summary goes through `ToSummaryLine`, and every other displayed transcript- or DB-derived
string (agent name, spawner, the tooltip's cwd, the worktree id) through `ToDisplayText`, which
strips control and format characters (bidi overrides, zero-width): it is untrusted text. The cwd a
resume launches from is never altered; only the tooltip's copy is.

**Read budget.** The catalog runs on the UI thread. The 512KB usage tail and the helper job scan are
OPTIONAL reads, made only for an entry that passed every rejection check and only while the
per-`List` byte budget (`DefaultReadByteBudget`, 24MB) has room; otherwise the entry is still listed
with `—` context or its fallback summary. The 256KB head and 64KB tail are always read.

## Why transcripts, not MT's session tables

`session_lineage` has no rows for spawned helpers and its `session_file_path` goes stale when Claude
Code moves a transcript into a worktree's folder. `SessionCatalog` therefore reads Claude Code's own
folders: `~/.claude/projects/<encoded root>` and every `<encoded root>--claude-worktrees-*` (plus `<encoded repo root>--claude-worktrees-*` for a project below its repo root, ticket 3fd3c9a7). Encoding
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
  `<root>\.claude\worktrees\<id>`, or, for a project registered at a SUBFOLDER of its git repo (ClarionAssistant), under `<repo root>\.claude\worktrees\<id>` (ticket 3fd3c9a7; the repo root is `SessionCatalog.FindRepoRoot` of the project root, used only as a strict non-drive ancestor, and carried on the entry as `WorktreeBase` so the re-checks use the same one). The repo-root worktrees are shared by every project in the repo, so a session admitted only through the repo root must also have BEGUN in this project: its first recorded cwd is exactly the project root (not below it: a registered project nested inside it would otherwise be offered here too), else it is skipped as "began in another project's folder of the same repository" (Codex review). A helper whose transcript starts directly in a repo-root worktree is therefore not offered (fail closed). A UNC path would otherwise make Windows authenticate to a remote
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

## Helpers resume with their PM (task 7d20e94c)

Resuming a PM session also reopens the helper panes it had open when it last ended. Owner decisions:
Continue mode (no dialog) resumes all of them and reports what it skipped; a helper whose Claude
process exited while its pane stayed open counts as CLOSED; one level only (no helpers of helpers),
at most 6. A new session never resumes helpers. Closing a PM's tab can close its helpers with it,
and they come back when the PM is resumed (item 8, below).

### The launch record

Nothing durable linked a PM session to its helpers: `SpawnedPaneRegistry` and `SpawnJobStore` are in
memory, `MULTITERMINAL_SPAWNER` is a name, and names repeat across days. So every pane launch is a row
in `pane_launches` (`Services/PaneLaunchRecorder`; schema in `database-tables.md`), keyed by launch
and session ids. MainForm writes it only through `Services/PaneLaunchTracker`:

| Event | Where | Write |
|---|---|---|
| `TerminalDocument.LaunchBegan` (every launch path, after `Start` returns) | `WirePaneLaunchRecording`, called at every `new TerminalDocument()` site next to `TerminalExited` | new row; a previous launch of the same pane still open is ended `process_exit` first ("Launch as" stops the process directly). A resume's row carries the resumed session id from the start, so a resumed pane closed before its first adoption is not left session-less (which let the older open row bring it back). `kind` is passed by the launch site where it knows it (`PaneLaunchKind.From(TerminalKind)`), else derived from the command |
| `ClaudeSessionAdopted` | same | `session_id` of the pane's current launch; a session CHANGE inside the launch (`/clear`, a fork) continues it on a new row, see below. Dropped when stale: the event is posted, so MainForm drops it unless the pane still holds that session, and the tracker drops it unless its launch generation is the current launch's |
| spawn | `OnSpawnRequested`, inside the Invoke after `AddNewTerminal` | `spawner_launch_id` = the PROVEN spawner pane's (nonce, never the typed name) current launch |
| close_helper | `OnSpawnedPaneCloseRequested`, BEFORE `doc.Close()` | `close_helper`; the `pane_closed` that Close raises then finds no open launch |
| PM tab closed, Owner answers Yes to "also close its helpers" (item 8) | `OnPaneFormClosing`, BEFORE each helper's `Close()` | each helper's launch `closed_with_pm`; their `pane_closed`, then the PM's own `pane_closed` as usual |
| pane removed from the dock | `OnDockContentRemoved` | `pane_closed` |
| process exit, pane left open | `OnTerminalExited` | `process_exit` |
| MT exits | `OnFormClosing`, right after the exit confirmation | every open row of this run `mt_exit`; see the guard below |
| MT crashes | nothing | rows stay open, which also reads as open |

**Spawner link in either order.** The helper's `LaunchBegan` fires inside `AddNewTerminal`, before
the caller knows the helper's DocId, so the link is normally made after the row exists. If it does
not exist yet, the spawner's launch (resolved at link time) is held per DocId and applied once when
the helper's launch begins; closing the pane first drops it.

**Order is `seq`, never `started_at`** (pipeline Run 1). Each insert takes `MAX(seq) + 1` in the same
statement, under the write gate. "Newest launch", "a LATER launch closed it" and the prior-launch
state all compare `seq`; two launches in one clock tick used to be ordered by whatever SQLite returned.

**`/clear` continues the launch on a new row** (pipeline Run 1, `TaskDatabase.AdoptPaneLaunchSession`,
atomic). When a pane's session changes INSIDE one launch and its open row already carries another
session, the row is ended `session_changed` and a continuation row (same pane, name, cwd, project,
kind, spawner link and MT run; new session; next `seq`) becomes the pane's current launch. Helpers
spawned before the change stay linked to the old row, so resuming the OLD session still offers
them; helpers spawned after link to the continuation and belong to the new session. A launch's FIRST
adoption never continues: it may simply replace the id a resume pre-filled. `session_changed` counts
as closed for the old session, which is correct for both readers: `GetOpenHelpers` does not look at
the PM rows' own state (only at their children), and for the prior-launch state no process runs the
old session any more, so it is `Ended` (the recency flag applies normally, no waiver). Shutdown
writes no adoption at all, so no continuation is opened after `mt_exit` was written.

**Threading.** Every caller is on the UI thread today (LaunchBegan is raised there; the adopted and
exited events are posted there; the spawn and close callbacks Invoke onto it). That is the callers'
accident, not the class's property, so the tracker's maps are under a lock; the SQLite writes
happen outside it.

### The shutdown guard, and why it is first

`OnFormClosing` stops every pane, and a stopped pane looks exactly like the Owner closing it (exit
and removal events). Without a guard every clean exit would mark every helper closed and nothing
could ever be resumed. So `PaneLaunchTracker.BeginShutdown` runs at the start of the REAL close path:
after the exit confirmation (a declined one returns before it and changes nothing) and before the
session save and the pane teardown. It marks every open launch of this run `mt_exit`
(`EndAllForShutdown`), and from then on the three close handlers write nothing. It is idempotent, so
the session-save pass's re-triggered `Close()` does nothing twice. A second close that is cancelled
after the confirmation (nothing does that today) would leave the tracker silent for the rest of the
run.

### Closing a PM closes its helpers (item 8)

The Owner often cannot exit all of MT (other terminals are working), so closing the PM's tab and then
each helper's tab used to end every helper `pane_closed`, and the resumed PM offered none. Now, when
a PM pane is closed, MT asks once (a plain `MessageBox`, like the exit confirmation): "Also close
Alice's 2 helpers (TestA, TestB)? They'll come back when you resume Alice." (`HelperCloseWithPm.Question`).

- **Where.** `TerminalDocument.FormClosing`, subscribed in `WirePaneLaunchRecording`
  (`MainForm.OnPaneFormClosing`). Every close of a docked pane goes through `Form.Close()` (the tab's
  ✕ via DockPanelSuite's `DockContentHandler.Close`, the tab menu's Close, and each pane of a batch
  close, below), so this is the one cancellable point BEFORE the pane leaves the dock;
  `OnDockContentRemoved` is too late to cancel.
- **Which helpers.** `PaneLaunchTracker.GetOpenHelperDocIds(pm)`: the panes whose CURRENT launch is
  linked to the PM's current launch (the tracker mirrors the spawner link per launch, carried across a
  helper's own `/clear`), filtered to panes still in the dock. Helpers spawned before the PM's `/clear`
  belong to the old session and are not offered; a helper whose process exited is closed already.
- **Yes** (default): each helper's launch is ended `closed_with_pm` FIRST (first end wins), then the
  pane is closed and disposed as `close_helper` does; then the PM closes normally. **No**: only the PM
  closes; helpers keep running (and their later tab close is a normal `pane_closed`). **Cancel**: the
  PM stays open.
- **Close All Tabs / Close all but this** (review fix). The tab menu does not close its fixed list
  one by one: `TerminalDocument.BatchCloseRequested` hands the panes (tab order) to
  `MainForm.OnPaneBatchCloseRequested`, and the pure `PaneBatchClose` (tests: `PaneBatchCloseTests`)
  decides order and skips. A pane is closed after every PM in the batch that lists it as an open
  helper, so the PM is asked before a helper whose tab comes first could be closed `pane_closed`. A
  close left docked (Cancel, or any other veto) STOPS the batch: nothing after it closes, its helpers
  included. Helpers handled by a PM's answer leave the batch: Yes already closed them; **No means they
  keep running even though Close All named them** (the Owner just said not to close them). A helper
  whose PM is NOT in the batch (Close all but this, on the PM) closes as an ordinary `pane_closed`;
  a PM's Yes closes every open helper it listed, including a "this" pane of Close all but this.
- **Not asked** during MT's shutdown (`IsShuttingDown`), for a pane MT is closing itself
  (`close_helper`, a helper being closed with its PM: `_panesClosedByMt`), or when no helper pane is
  open. A second close gesture while the question is up is swallowed.
- **Decision: a PM whose own process already exited is still asked.** Home or claude ending leaves
  the pane open with its launch ended `process_exit`; the tracker keeps that ended launch for the
  pane until it relaunches or closes, because the helpers are still running and still belong to
  that session, and they are what the Owner would lose.

`closed_with_pm` is resumable wherever `mt_exit` is: `GetOpenHelpers` keeps a child ended so, and a
LATER launch of the same helper session ended `closed_with_pm` does not count as "closed later" (a
later `pane_closed` still does). Its prior-launch state is `ClosedWithPm` (next section), so a helper
closed seconds ago is not skipped by the recency flag.

### Choosing and resuming

- **Dialog.** "Also resume helpers" lists `GetOpenHelpers` for the selected row, else the Continue
  last candidate (name, where, started), all ticked; click or Space toggles. An untick is remembered
  per (session, helper) in `HelperTickState`, so a session the Owner never looked at resumes all its
  helpers. Helpers whose session is live now are not listed. The lookup is
  `LaunchSessionChoiceModel.HelpersFor`, cached per session and called on selection change, never
  while painting. No helpers: one dimmed "—" row.
- **Wiring.** After the PM's `StartTerminal` on a resume, `QueueHelperResume` posts
  `ResumeHelpersFor`, which runs only while the PM pane is still running the launch that resumed that
  session (a refused or replaced PM takes no helpers).
- **Per helper**, the PM resume's own rules (`HelperResume.Check`): rebuild the live set (it includes
  the PM and the helpers already reopened), `SessionCatalog.TryGetEntry` by id, the model's "may be
  open elsewhere" rule with the launch record's verdict (below; a flagged helper is SKIPPED, not
  confirmed, since there is no one to confirm), `RecheckResume`, `ValidateResumeCwd`, and the folder
  still existing.
  Then `BuildCommand(ClaudeCode, project, cwd, sessionId)` as for the PM, and `AddNewTerminal` with
  the original name (atomic: a held name comes back suffixed), the PM as spawner, the PM's profile
  inherited, unattended (a profile refusal is a skip), `resumeSessionId` (reserves
  `PendingResumeSessionId`) and `pinWorkingDirectory` (no `ResolveSpawnDir`: the repo-root move and the
  task-worktree cd would both cause "No conversation found").
- **Re-link.** `SpawnService.Panes.Record(helper, name, pm DocId, pm nonce)`, so `close_helper` works
  from the resumed PM, and the helper's launch row is linked to the PM's current launch.
- **One summary**, and only if something was skipped or renamed ("Notes came back as Notes-2"; the PM's
  conversation still uses the old name).

### Previous-run launches are not "open elsewhere"

The recent-write flag (transcript written in the last ~2 minutes) exists for processes OUTSIDE MT.
Applied blindly it defeats the main scenario: MT exits or crashes, the Owner relaunches within a
minute, and every session that was working up to that moment is flagged, so the PM needs a confirm
and every helper is skipped. So the flag consults the launch record
(`PaneLaunchRecorder.GetPriorLaunchState`, the session's NEWEST `pane_launches` row against the
recorder's `MtRunId`; pure rule `ClassifyPriorLaunch`):

| Newest row | State | Effect on "may be open elsewhere" |
|---|---|---|
| none, or the read failed | `NoRecord` | recency flag applies as before |
| previous run, still open (crash) or `mt_exit`, and that MT process provably gone | `DiedWithPreviousRun` | recency flag WAIVED: that process died with that MT |
| previous run, still open or `mt_exit`, MT process NOT provably gone (alive, never recorded, unreadable) | `OpenElsewhere` | PM: recency flag applies as for `NoRecord`; helper: skipped as "may still be open elsewhere" whatever the transcript's age |
| this run, not ended | `OpenInCurrentRun` | flagged open (PM: confirm; helper: skipped as already open) |
| ended `closed_with_pm`, any run (item 8) | `ClosedWithPm` | recency flag WAIVED: MT closed that pane itself, and disposing it terminates its process tree. Without this a helper closed with its PM seconds ago would be skipped as "may be open elsewhere" |
| this run, ended `pane_closed`, `close_helper` or `process_exit` | `ClosedInCurrentRun` | recency flag WAIVED: this MT ended that process (disposing the pane kills its job) or watched it exit. Without this a session whose tab was closed a minute ago showed ⚠ "may be open elsewhere" (live test, 2026-10-21) |
| anything else (this run's `session_changed` or `mt_exit`, or closed in an earlier run) | `Ended` | recency flag applies as before |

**Fail-closed on the previous run** (pipeline Run 1). Each recorder writes its MT process id and
process start time to `mt_runs` under its run id. A previous run's open row counts as dead only when
`MtProcessProbe.Probe` says that process is gone: no process has the id, or one has it with another
start time (the id was reused), or it has exited. Alive, unknown (access denied) or unrecorded (a run
from before `mt_runs`) is `OpenElsewhere`. Today the waiver was already safe for two reasons the code
states in `ClassifyPriorLaunch`: `SingleInstanceGuard` holds `Local\MultiTerminal.SingleInstance` for
the whole process, so a new run cannot start while the old one lives; and every pane tree is in a
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` job (`ConPtyTerminal.CreateJobAndAssignProcess`), so the panes die
with MT even on a crash. Neither is airtight (the guard fails open, `Local\` does not cover another
Windows session, the job assignment is best-effort), which is why the probe decides.
`ClosedWithPm` rests on the same job kill (`TerminalDocument.Dispose` stops the terminal, which
terminates its job), with no probe: the close happened in the run that wrote the row, and a helper
the job assignment missed is the same best-effort gap as above.

It applies to both the PM's offer (`LaunchSessionChoiceModel`'s `priorLaunch` argument, asked once per
offered session in `LoadLaunchSessionChoices`) and each helper (`HelperResume.Check`'s `prior`). The
waiver touches only the recency flag: this run's live set (`CurrentClaudeSessionId`,
`PendingResumeSessionId`, `AgentProcess.SessionId`), the unknown-pane rule and the pre-launch
`RecheckResume` all still apply. Deliberately narrow: a session closed in an earlier run, or whose
process went on under a new session (`session_changed`), keeps the recency flag. The record cannot see a process outside MT in any case, so a
session resumed in a CA tab after an `mt_exit` is also unflagged; that is accepted, because the flag
was always advisory and a quick relaunch after an exit is the case this feature exists for.

### Known gaps

- Item 8's question is asked from `FormClosing`, which is executed live only: no test drives a
  DockPanelSuite tab close. The tracker's choice of helpers, the end-reason order and the resume rules
  are unit-tested (`ClosedWithPmTests`).
- A form whose handle was never created closes by `Dispose` without `FormClosing`, so it is not asked
  about; every visible pane has a handle.

- Only sessions launched after this shipped have rows; older helpers are never offered.
- A pane whose session id MT never learns (a project statusLine that bypasses MT's) has no
  `session_id`, so it is never offered as a helper.
- A resumed helper is a spawned pane with no job: its SessionStart briefing asks it to collect one,
  and the collect answers `no_job`.
- The resume happens on the UI thread, one helper after another (each a catalog lookup and a pane).
- Launches recorded by a run before `mt_runs` existed read as `OpenElsewhere` to every later run (their
  helpers are skipped once, as "may be open elsewhere").

## Restore at startup (task 3e1c09b8)

When MT starts after a run that ended (clean exit OR crash) with Claude terminals open, one dialog
(`Dialogs/StartupRestoreDialog`) lists them, all ticked: "The following terminals were open when you
closed MultiTerminal. Would you like to resume the checked sessions?" Buttons: Resume N terminals /
Don't restore (closing the window is Don't restore). It replaced ticket f7897fa1's plan.

**Source: the launch record, not settings.** `SessionTerminals` and layout.xml are written only on a
clean exit and carry no session id. `PaneLaunchRecorder.GetPreviousRunOpenPanes`:
- the previous run is the `mt_runs` row recorded just BEFORE this run (`TaskDatabase.GetPreviousMtRunId`),
  never "the newest run that launched something", so after a Don't restore the next start offers nothing;
- read only when that run's MT process is provably gone (same fail-closed probe as `ClassifyPriorLaunch`);
- pure rule `SelectRestorable`: per pane (DocId) its newest launch by `seq`, kept when still open or
  `mt_exit`, kind `claude`, with a session id; one pane per session; a pane's parent is the kept pane
  with the spawner row's DocId (so a PM's `/clear` continuation keeps its helpers); one level only.

**Rows (`Services/StartupRestore.cs`, pure, `StartupRestoreTests`).** Top-level panes need a registered
project (the resume validates the cwd against its root); one without is not listed, nor are its helpers.
Helpers are indented under their PM and use the PM's project. Unticking a PM unticks and disables its
helpers; a helper comes back only with its PM. Not listed: Home panes, PowerShell and Codex panes.

**The launch path is the ordinary one.** `MainForm.OfferStartupRestore` (posted from `Shown` after
`RestoreSession`) shows the dialog, then `RunStartupRestore`:
1. Each ticked PM is checked with `HelperResume.Check` (no one to confirm "may be open elsewhere" with,
   so a flagged session is skipped), then a `PresetLaunchChoice` is stored for the pane and
   `OnStartScreenProjectLaunched` runs; `TryChooseLaunchSession` consumes the preset instead of the
   dialog. So the profile decision, `ValidateResumeCwd`, `RecheckResume`, name registration and
   `--resume` are exactly a launch dialog resume's. The ticked helpers go through `QueueHelperResume`.
2. Placement (`StartupRestorePlacement.Match`): a session goes into the restored Home pane whose title
   equals its name (ordinal-ignore-case, untrimmed), else a new pane. A helper is created by
   `AddNewTerminal` as before and then moved into its restored pane's dock pane (`MoveIntoRestoredPane`).
3. Restored panes nobody went into are closed (`PanesToClose`, `CloseUnusedRestorePanes`, which never
   closes a started pane or the last terminal pane); when nothing came back, the first stays, so MT
   opens on ONE Home pane. The same collapse (`CollapseToOneHomePane`) runs when there is nothing to
   offer at all (no open Claude pane in the previous run, a previous run not provably gone, every pane
   without a registered project, or a failed read): no dialog, one Home pane (Owner decision 2026-10-21).
4. One summary lists the PMs not resumed; each PM's helper summary is the existing one.
5. Feedback (Owner live test): before the first launch every pane about to be restored shows "Restoring
   <name>…" (`TerminalDocument.ShowRestoringNotice`, a WinForms label painted synchronously in place of
   the start screen, removed by `ShowStartScreen`/`HideStartScreen`) and the cursor waits. Needed because
   a launch made while the startup code-graph reindex holds the database freezes the UI thread for ~14s
   in its registration writes (ticket bb1d9481 is the cure; the notice only makes the wait visible).

**Known gaps.**
- The layout restore still pairs layout.xml panes with `SessionTerminals` by POSITION (finding in
  f7897fa1); a pane that layout.xml lacks gets a new pane, so it comes back, but not in its old place.
- After a crash layout.xml is from the last CLEAN exit, so name matching may miss.
- A PM whose launch is refused shows that launch's own message box as well as the summary line.
- Executed live only: no test drives the dialog, the preset launch or the pane moves.
